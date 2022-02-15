﻿using Hydra.Http11;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Hydra
{
    /// <summary>
    /// An HTTP protocol version
    /// </summary>
    public enum HttpVersion
    {
        /// <summary>
        /// HTTP/1.0
        /// </summary>
        Http10,
        /// <summary>
        /// HTTP/1.1
        /// </summary>
        Http11,
    }

    /// <summary>
    /// An HTTP request
    /// </summary>
    public class HttpRequest
    {
        /// <summary>
        /// Underlying socket the request originated from
        /// </summary>
        internal readonly Socket socket;
        internal readonly HttpReader reader;
        private Stream? body = null;

        /// <summary>
        /// Request method
        /// </summary>
        public string Method { get; set; }
        /// <summary>
        /// Request URI
        /// 
        /// Contains the absolute path and query
        /// </summary>
        public string Uri { get; set; }
        /// <summary>
        /// Request protocol version
        /// </summary>
        public HttpVersion Version { get; }
        /// <summary>
        /// Request headers
        /// </summary>
        public HttpHeaders Headers { get; } = new();
        /// <summary>
        /// Request body
        /// </summary>
        /// <exception cref="HeadersNotReadException">
        /// Thrown when trying to access the body before the headers have been read
        /// </exception>
        public Stream Body
        {
            get => body ?? throw new HeadersNotReadException();
            private set => body = value;
        }
        /// <summary>
        /// Request body encoding
        /// 
        /// The Transfer-Encoding and Content-Encoding headers should be left intact
        /// and users should instead push and pop from this stack to indicate the
        /// current encoding layers of the body
        /// </summary>
        public Stack<string> Encoding { get; } = new();

        /// <summary>
        /// Endpoint of the client the request originated from
        /// 
        /// In most cases an <see cref="IPEndPoint"/> but can be a <see cref="UnixDomainSocketEndPoint"/>
        /// if the server is listening on a Unix socket
        /// </summary>
        public EndPoint? Remote => socket.RemoteEndPoint;

        /// <summary>
        /// Cancellation token passed to the server at startup if any
        /// </summary>
        public CancellationToken CancellationToken { get; }

        internal HttpRequest(
            string method,
            string uri,
            int version,
            Socket socket,
            HttpReader reader,
            CancellationToken cancellationToken)
        {
            Method = method;
            Uri = uri;
            Version = version switch
            {
                0 => HttpVersion.Http10,
                1 => HttpVersion.Http11,
                _ => throw new NotSupportedException(),
            };
            CancellationToken = cancellationToken;

            this.socket = socket;
            this.reader = reader;
        }

        /// <summary>
        /// Reads the request headers if they haven't been yet
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async ValueTask ReadHeaders()
        {
            if (body != null) return;
            if (!await reader.ReadHeaders(Headers, CancellationToken)) throw new ConnectionClosedException();
            Validate();
        }

        /// <summary>
        /// Validates the request
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Validate()
        {
            // HTTP/1.1 requests must contain a Host header
            if (Version == HttpVersion.Http10 && (!Headers.TryGetValue("Host", out var host) || host.Count > 1)) throw new InvalidHostException();

            // requests containing both Transfer-Encoding and Content-Length headers are invalid and should be rejected
            if (Headers.ContainsKey("Transfer-Encoding") && Headers.ContainsKey("Content-Length")) throw new TransferEncodingAndContentLengthException();

            // push content encodings to our stack first if any
            if (Headers.TryGetValue("Content-Encoding", out var ce))
            {
                foreach (string value in ce) Encoding.Push(value);
            }

            if (Headers.TryGetValue("Transfer-Encoding", out var te))
            {
                // push transfer encodings to our stack
                foreach (string value in te) Encoding.Push(value);

                // if we have transfer encodings the outermost one must be chunk or the body isn't readable
                if (Encoding.TryPeek(out string? e) && e.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    body = new HttpChunkedBodyStream(reader.Reader, Headers);
                    Encoding.Pop();
                }
                else throw new UnknownBodyLengthException();
            }
            else if (Headers.TryGetValue("Content-Length", out var cl))
            {
                int length;

                // there is one content length value but it's not readable as an integer
                if (cl.Count == 1)
                {
                    if (!int.TryParse(cl, out length)) throw new InvalidContentLengthException();
                }
                else
                {
                    // if there are many content length values they must all be identical and readable as integers
                    string[] distinct = cl.Distinct().ToArray();
                    if (distinct.Length != 1 || !int.TryParse(distinct[0], out length)) throw new InvalidContentLengthException();
                }

                body = new SizedStream(reader.Body, length);
            }
            // if a request has neither Transfer-Encoding nor Content-Length headers it is assumed to have an empty body
            else body = EmptyStream.Body;
        }

        /// <summary>
        /// Drains the headers and body to prepare the underlying connection for the next request
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal async ValueTask Drain()
        {
            await ReadHeaders();

            var pool = ArrayPool<byte>.Shared;
            byte[] buffer = pool.Rent(4096);
            int read = 1;

            try
            {
                while (read > 0) read = await body!.ReadAsync(buffer, CancellationToken);
            }
            finally
            {
                pool.Return(buffer);
            }
        }

        /// <summary>
        /// An exception thrown by the server when trying to access the body
        /// before the headers have been read
        /// </summary>
        public class HeadersNotReadException : Exception { }

        /// <summary>
        /// An exception thrown by the server if a request has both
        /// Transfer-Encoding and Content-Length headers
        /// 
        /// The spec recommends rejecting such requests because they have a
        /// high chance of being spoofed.
        /// </summary>
        public class TransferEncodingAndContentLengthException : HttpBadRequestException { }
        /// <summary>
        /// An exception thrown by the server if a request has an invalid
        /// Content-Length header
        /// </summary>
        public class InvalidContentLengthException : HttpBadRequestException { }
        /// <summary>
        /// An exception thrown by the server if a request has an invalid
        /// or missing Host header
        /// </summary>
        public class InvalidHostException : HttpBadRequestException { }
        /// <summary>
        /// An exception thrown by the server if it can't determine the length of a request body.
        /// </summary>
        public class UnknownBodyLengthException : HttpBadRequestException { }
    }

    public static class ReaderExtensions
    {
        /// <summary>
        /// Attempts to read a request from the underlying connection into a structured class
        /// </summary>
        /// <param name="socket">Underlying socket the request originated from</param>
        /// <returns>An instance of <see cref="HttpRequest"/> or null if the connection is closed before receiving a full request</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static async ValueTask<HttpRequest?> ReadRequest(this HttpReader reader, Socket socket, CancellationToken cancellationToken = default)
        {
            var startLineResult = await reader.ReadStartLine(cancellationToken);
            if (!startLineResult.Complete(out var startLine)) return null;

            return new HttpRequest(
                startLine.Value.Method,
                startLine.Value.Uri,
                startLine.Value.Version,
                socket,
                reader,
                cancellationToken);
        }

        /// <summary>
        /// Attempts to read a collection of headers from the underlying connection into the provided instance
        /// </summary>
        /// <param name="headers">Instance to write the read headers to</param>
        /// <returns>true on success or false if the connection is closed before receiving the full headers</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static async ValueTask<bool> ReadHeaders(this AbstractReader reader, HttpHeaders headers, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var headerResult = await reader.ReadHeader(cancellationToken);
                if (headerResult.Complete(out var header))
                {
                    string name = header.Value.Name;
                    string[] values = header.Value.Value.Split(',').Select((s) => s.Trim()).ToArray();
                    headers.Add(name, values);
                }
                else if (headerResult.Incomplete) return false;
                else if (headerResult.Finished) return true;
            }
        }
    }
}