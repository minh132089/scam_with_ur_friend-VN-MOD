// Minimal HTTP/1.1 client for a server on this machine (VieNeu). The game's stripped System.dll removes most of
// HttpWebRequest (no request body, no timeouts), so this talks to the socket directly. Supports Content-Length,
// read-until-close and chunked transfer encoding.
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SwyfCompat
{
    public sealed class LocalHttpResponse : IDisposable
    {
        public int Status;
        public Stream Body;
        internal TcpClient Client;
        internal CancellationTokenRegistration Registration;

        public string ReadAllText()
        {
            using (StreamReader r = new StreamReader(Body, Encoding.UTF8)) return r.ReadToEnd();
        }

        public void Dispose()
        {
            try { Registration.Dispose(); } catch (Exception) { }
            try { Client.Close(); } catch (Exception) { }
        }
    }

    public static class LocalHttp
    {
        public static LocalHttpResponse Send(string method, Uri uri, byte[] body, string contentType, int timeoutMs, CancellationToken ct)
        {
            TcpClient client = new TcpClient();
            client.ReceiveTimeout = timeoutMs;
            client.SendTimeout = timeoutMs;
            client.NoDelay = true;
            LocalHttpResponse response = new LocalHttpResponse { Client = client };
            try
            {
                IAsyncResult connect = client.BeginConnect(uri.Host, uri.Port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(Math.Min(timeoutMs, 3000))) throw new IOException("connect timeout");
                client.EndConnect(connect);
                response.Registration = ct.Register(() => { try { client.Close(); } catch (Exception) { } });
                NetworkStream ns = client.GetStream();

                StringBuilder head = new StringBuilder();
                head.Append(method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
                head.Append("Host: ").Append(uri.Host).Append(':').Append(uri.Port).Append("\r\n");
                head.Append("Connection: close\r\nAccept: */*\r\n");
                if (body != null)
                {
                    head.Append("Content-Type: ").Append(contentType ?? "application/octet-stream").Append("\r\n");
                    head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
                }
                head.Append("\r\n");
                byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
                ns.Write(headBytes, 0, headBytes.Length);
                if (body != null && body.Length > 0) ns.Write(body, 0, body.Length);
                ns.Flush();

                string statusLine = ReadLine(ns);
                string[] parts = statusLine.Split(' ');
                if (parts.Length < 2 || !int.TryParse(parts[1], out response.Status)) throw new IOException("bad HTTP status line: " + statusLine);
                bool chunked = false;
                long length = -1;
                string line;
                while ((line = ReadLine(ns)).Length > 0)
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = line.Substring(0, colon).Trim().ToLowerInvariant();
                    string value = line.Substring(colon + 1).Trim();
                    if (name == "transfer-encoding" && value.ToLowerInvariant().Contains("chunked")) chunked = true;
                    else if (name == "content-length") long.TryParse(value, out length);
                }
                response.Body = chunked ? (Stream)new ChunkedStream(ns) : new LimitedStream(ns, length);
                return response;
            }
            catch (Exception)
            {
                response.Dispose();
                ct.ThrowIfCancellationRequested();
                throw;
            }
        }

        internal static string ReadLine(Stream s)
        {
            StringBuilder b = new StringBuilder();
            while (true)
            {
                int c = s.ReadByte();
                if (c < 0) { if (b.Length == 0) throw new IOException("connection closed"); break; }
                if (c == '\n') break;
                if (c != '\r') b.Append((char)c);
                if (b.Length > 16384) throw new IOException("header line too long");
            }
            return b.ToString();
        }
    }

    internal sealed class LimitedStream : Stream
    {
        private readonly Stream inner;
        private long remaining;   // -1 = until close
        public LimitedStream(Stream inner, long length) { this.inner = inner; remaining = length; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining == 0) return 0;
            if (remaining > 0 && count > remaining) count = (int)remaining;
            int n = inner.Read(buffer, offset, count);
            if (n > 0 && remaining > 0) remaining -= n;
            return n;
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    internal sealed class ChunkedStream : Stream
    {
        private readonly Stream inner;
        private long chunkLeft;
        private bool done;
        public ChunkedStream(Stream inner) { this.inner = inner; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (done) return 0;
            if (chunkLeft == 0)
            {
                string sizeLine = LocalHttp.ReadLine(inner);
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                chunkLeft = Convert.ToInt64(sizeLine.Trim(), 16);
                if (chunkLeft == 0)
                {
                    done = true;
                    try { while (LocalHttp.ReadLine(inner).Length > 0) { } } catch (IOException) { }
                    return 0;
                }
            }
            int n = inner.Read(buffer, offset, (int)Math.Min(count, chunkLeft));
            if (n <= 0) throw new IOException("connection closed inside chunk");
            chunkLeft -= n;
            if (chunkLeft == 0) LocalHttp.ReadLine(inner); // CRLF after chunk data
            return n;
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
