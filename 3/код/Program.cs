using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace чат
{
    class Program
    {
        private static readonly object ConsoleSync = new object();

        static async Task Main(string[] args)
        {
            SafeWriteLine("~ Консольный чат ~");

            SafeWrite("Выберите режим [server/client]: ");
            string mode = Console.ReadLine()?.Trim().ToLower();
            if (mode != "server" && mode != "client")
            {
                SafeWriteLine("Неверный режим. Завершение.");
                return;
            }

            if (mode == "server")
            {
                SafeWrite("Введите IP сервера: ");
                string ipInput = Console.ReadLine()?.Trim();
                IPAddress serverIp = string.IsNullOrWhiteSpace(ipInput) ? IPAddress.Any : IPAddress.Parse(ipInput);

                SafeWrite("Введите порт: ");
                if (!int.TryParse(Console.ReadLine(), out int port) || port < 1024 || port > 65535)
                {
                    SafeWriteLine("Некорректный порт."); return;
                }

                if (!IsPortAvailable(serverIp, port))
                {
                    SafeWriteLine($"Порт {port} недоступен."); return;
                }

                await RunServerAsync(serverIp, port);
            }
            else
            {
                SafeWrite("Введите IP сервера: ");
                string targetIp = Console.ReadLine()?.Trim();
                if (string.IsNullOrWhiteSpace(targetIp) || !IPAddress.TryParse(targetIp, out _))
                {
                    SafeWriteLine("Некорректный IP сервера."); return;
                }

                SafeWrite("Введите порт сервера: ");
                if (!int.TryParse(Console.ReadLine(), out int port) || port < 1024 || port > 65535)
                {
                    SafeWriteLine("Некорректный порт."); return;
                }

                SafeWrite("Введите локальный IP для этого клиента: ");
                string localIp = Console.ReadLine()?.Trim() ?? "127.0.0.1";
                if (!IPAddress.TryParse(localIp, out _))
                {
                    SafeWriteLine("Некорректный локальный IP."); return;
                }

                await RunClientAsync(targetIp, port, localIp);
            }
        }

        #region Вспомогательные методы
        static bool IsPortAvailable(IPAddress addr, int port)
        {
            try { using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); s.Bind(new IPEndPoint(addr, port)); s.Close(); return true; }
            catch { return false; }
        }
        static void SafeWriteLine(string m) { lock (ConsoleSync) Console.WriteLine(m); }
        static void SafeWrite(string m) { lock (ConsoleSync) Console.Write(m); }
        #endregion

        #region Сервер
        static async Task RunServerAsync(IPAddress bindIp, int port)
        {
            var serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var clients = new Dictionary<Socket, string>();
            var lockObj = new object();

            try
            {
                serverSocket.Bind(new IPEndPoint(bindIp, port));
                serverSocket.Listen(10);
                SafeWriteLine($"Сервер запущен: {bindIp}:{port}");
                SafeWriteLine("Клиентам будут назначены виртуальные IP в сообщениях.");
                SafeWriteLine("Введите '/quit' для выхода.\n");

                _ = Task.Run(async () =>
                {
                    byte vipOctet = 2;
                    while (true)
                    {
                        try
                        {
                            Socket client = await serverSocket.AcceptAsync();
                            string vip = $"127.0.0.{vipOctet++}";
                            if (vipOctet > 254) vipOctet = 2;

                            lock (lockObj) clients[client] = vip;
                            SafeWriteLine($"{vip} подключился ({client.RemoteEndPoint})");
                            await client.SendAsync(Encoding.UTF8.GetBytes($"[SERVER]|Ваш виртуальный IP: {vip}\n"), SocketFlags.None);
                            _ = HandleClientAsync(client, clients, lockObj);
                        }
                        catch (ObjectDisposedException) { break; }
                        catch (Exception ex) { SafeWriteLine($"{ex.Message}"); }
                    }
                });

                while (true) { if (Console.ReadLine()?.Trim().ToLower() == "/quit") break; }
            }
            finally { serverSocket?.Close(); SafeWriteLine("Сервер остановлен."); }
        }

        static async Task HandleClientAsync(Socket client, Dictionary<Socket, string> clients, object lockObj)
        {
            var buf = new byte[4096]; var sb = new StringBuilder();
            try
            {
                while (true)
                {
                    int r = await client.ReceiveAsync(buf, SocketFlags.None);
                    if (r == 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r));
                    string t = sb.ToString(); int nl;
                    while ((nl = t.IndexOf('\n')) >= 0)
                    {
                        string msg = t.Substring(0, nl).Trim();
                        t = t.Substring(nl + 1);
                        if (!string.IsNullOrEmpty(msg) && clients.TryGetValue(client, out string vip))
                        {
                            SafeWriteLine($"[{vip}]: {msg}");
                            await BroadcastAsync($"[{vip}]|{msg}\n", client, clients, lockObj);
                        }
                    }
                    sb.Clear(); sb.Append(t);
                }
            }
            catch { }
            finally
            {
                if (clients.TryGetValue(client, out string virtualIp))
                {
                    SafeWriteLine($"\nКлиент {virtualIp} отключился от сервера.");
                }

                lock (lockObj)
                {
                    clients.Remove(client);
                }

                // отправляет FIN пакет
                try { client.Shutdown(SocketShutdown.Both); } catch { }
                client.Close();
            }
        }

        static async Task BroadcastAsync(string msg, Socket sender, Dictionary<Socket, string> clients, object lockObj)
        {
            byte[] data = Encoding.UTF8.GetBytes(msg);
            Dictionary<Socket, string> snap;
            lock (lockObj) snap = new Dictionary<Socket, string>(clients);
            foreach (var (c, _) in snap)
                if (c != sender && c.Connected) try { await c.SendAsync(data, SocketFlags.None); } catch { }
        }
        #endregion

        #region Клиент
        static async Task RunClientAsync(string serverIp, int port, string localIp)
        {
            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                //  привязка конкретного ip клиента дло полключения
                clientSocket.Bind(new IPEndPoint(IPAddress.Parse(localIp), 0));

                SafeWriteLine($"Подключение с {localIp} -> {serverIp}:{port}...");
                await clientSocket.ConnectAsync(serverIp, port);
                SafeWriteLine("Подключено! Введите сообщение или '/exit' для выхода:\n");

                _ = ReceiveMessagesAsync(clientSocket);

                while (true)
                {
                    string input = Console.ReadLine();
                    if (input == null) break;
                    string t = input.Trim();
                    if (t.ToLower() == "/exit") break;
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    await clientSocket.SendAsync(Encoding.UTF8.GetBytes(t + "\n"), SocketFlags.None);
                }
            }
            catch (SocketException ex)
            {
                SafeWriteLine($"Ошибка сети: {ex.Message}\nУбедитесь, что IP {localIp} добавлен в loopback-интерфейс ОС.");
            }
            finally
            {
                try
                {
                    // FIN обеим сторонам
                    clientSocket?.Shutdown(SocketShutdown.Both);
                }
                catch (ObjectDisposedException) { }
                catch (SocketException) { } // Игнорируем ошибки, если сокет уже закрыт

                // освобождаем ресурсы сокета
                clientSocket?.Close();

                SafeWriteLine("Соединение закрыто.");
            }
        }

        static async Task ReceiveMessagesAsync(Socket s)
        {
            var buf = new byte[4096]; var sb = new StringBuilder();
            try
            {
                while (true)
                {
                    int r = await s.ReceiveAsync(buf, SocketFlags.None);
                    if (r == 0) { SafeWriteLine("\nСервер разорвал соединение."); break; }
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r));
                    string t = sb.ToString(); int nl;
                    while ((nl = t.IndexOf('\n')) >= 0)
                    {
                        string m = t.Substring(0, nl); t = t.Substring(nl + 1);
                        if (!string.IsNullOrEmpty(m))
                        {
                            SafeWriteLine($"\n {m}");
                            SafeWrite(" ");
                        }
                    }
                    sb.Clear(); sb.Append(t);
                }
            }
            catch { }
        }
        #endregion
    }
}