using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FileStorageServer
{
    public class Program
    {
        private static readonly string StorageRoot = Path.Combine(Directory.GetCurrentDirectory(), "storage");
        private static readonly Dictionary<string, Func<HttpListenerContext, Task>> Routes = new();

        public static async Task Main(string[] args)
        {
            if (!Directory.Exists(StorageRoot)) Directory.CreateDirectory(StorageRoot);
            InitializeRoutes();

            using var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8080/");
            listener.Start();
            Console.WriteLine("Server started: http://localhost:8080/");

            while (true)
            {
                var ctx = await listener.GetContextAsync();
                _ = DispatchRequestAsync(ctx);
            }
        }

        private static void InitializeRoutes()
        {
            Routes["GET"] = HandleGet;
            Routes["PUT"] = HandlePut;
            Routes["HEAD"] = HandleHead;
            Routes["DELETE"] = HandleDelete;
        }

        private static async Task DispatchRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                var relPath = ctx.Request.Url.AbsolutePath.TrimStart('/');
                var fullPath = Path.GetFullPath(Path.Combine(StorageRoot, relPath));
                var rootPath = Path.GetFullPath(StorageRoot);

                if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    SendError(ctx.Response, 403, "Forbidden");
                    return;
                }

                var method = ctx.Request.HttpMethod.ToUpperInvariant();
                if (Routes.TryGetValue(method, out var handler))
                {
                    await handler(ctx);
                }
                else
                {
                    SendError(ctx.Response, 405, "Method Not Allowed");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex.Message}");
                try { SendError(ctx.Response, 500, "Internal Error"); } catch { }
            }
        }

        private static async Task HandleGet(HttpListenerContext ctx)
        {
            var path = ResolvePath(ctx);
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                ctx.Response.ContentType = GetMimeType(path);
                ctx.Response.ContentLength64 = info.Length;
                ctx.Response.Headers["Last-Modified"] = info.LastWriteTimeUtc.ToString("R");
                using var fs = File.OpenRead(path);
                await fs.CopyToAsync(ctx.Response.OutputStream);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            else if (Directory.Exists(path))
            {
                var files = Directory.GetFiles(path).Select(Path.GetFileName).OrderBy(f => f);
                var dirs = Directory.GetDirectories(path).Select(Path.GetFileName).OrderBy(d => d);
                var json = JsonSerializer.Serialize(new { path = "/" + ctx.Request.Url.AbsolutePath.TrimStart('/').TrimEnd('/'), files, directories = dirs }, new JsonSerializerOptions { WriteIndented = true });
                var buf = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buf.Length;
                await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            else SendError(ctx.Response, 404, "Not Found");
        }

        private static async Task HandlePut(HttpListenerContext ctx)
        {
            var path = ResolvePath(ctx);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            bool existed = File.Exists(path);
            using var fs = File.Create(path);
            await ctx.Request.InputStream.CopyToAsync(fs);
            ctx.Response.StatusCode = existed ? 200 : 201;
            ctx.Response.Close();
            Console.WriteLine($"{(existed ? "Updated" : "Created")}: {ctx.Request.Url.AbsolutePath}");
        }

        private static async Task HandleHead(HttpListenerContext ctx)
        {
            var path = ResolvePath(ctx);
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                ctx.Response.ContentLength64 = info.Length;
                ctx.Response.Headers["Last-Modified"] = info.LastWriteTimeUtc.ToString("R");
                ctx.Response.ContentType = GetMimeType(path);
            }
            else if (Directory.Exists(path))
            {
                ctx.Response.Headers["X-Total-Items"] = (Directory.GetFiles(path).Length + Directory.GetDirectories(path).Length).ToString();
                ctx.Response.ContentType = "application/json";
            }
            else { SendError(ctx.Response, 404, "Not Found"); return; }

            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        }

        private static async Task HandleDelete(HttpListenerContext ctx)
        {
            var path = ResolvePath(ctx);
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
            else { SendError(ctx.Response, 404, "Not Found"); return; }

            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            Console.WriteLine($"Deleted: {ctx.Request.Url.AbsolutePath}");
        }

        private static string ResolvePath(HttpListenerContext ctx) =>
            Path.Combine(StorageRoot, ctx.Request.Url.AbsolutePath.TrimStart('/'));

        private static void SendError(HttpListenerResponse r, int code, string msg)
        {
            r.StatusCode = code;
            var b = Encoding.UTF8.GetBytes(msg);
            r.ContentLength64 = b.Length;
            r.OutputStream.Write(b, 0, b.Length);
            r.Close();
        }

        private static string GetMimeType(string p) => Path.GetExtension(p)?.ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }
}
