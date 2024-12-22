using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NLog;

class ChatApplication
{
    private static Logger Logger = LogManager.GetCurrentClassLogger();
    private string host;
    private int tcpPort;
    private int udpPort;

    // Словарь для хранения TCP клиентов и их имен
    private readonly Dictionary<string, StreamWriter> connectedClients = new();

    public ChatApplication()
    {
        LoadConfig();
    }
    private UdpClient CreateUdpClient(int port)
    {
        var udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        // Устанавливаем параметр SO_REUSEADDR для возможности общего доступа к порту
        udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        udpSocket.Bind(new IPEndPoint(IPAddress.Any, port));

        var udpClient = new UdpClient
        {
            Client = udpSocket // Устанавливаем сокет
        };

        return udpClient;
    }
    public async Task RunAsync()
    {
        try
        {
            var tcpListener = new TcpListener(IPAddress.Parse(host), tcpPort);
            var udpClient =  CreateUdpClient(udpPort);

            tcpListener.Start();
            Console.WriteLine($"Сервер TCP запущен на {host}:{tcpPort}");
            Console.WriteLine($"Сервер UDP запущен на {host}:{udpPort}");

            Logger.Info($"Сервер TCP запущен на {host}:{tcpPort}");
            Logger.Info($"Сервер UDP запущен на {host}:{udpPort}");

            // Обработка TCP соединений
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var tcpClient = await tcpListener.AcceptTcpClientAsync();
                    _ = HandleTcpClientAsync(tcpClient);
                }
            });

            // Обработка UDP сообщений
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var result = await udpClient.ReceiveAsync();
                    string message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} UDP: {Encoding.UTF8.GetString(result.Buffer)}";
                    Console.WriteLine(message);
                    Logger.Info(message);
                }
            });

            // Отправка сообщений
            await SendMessageAsync(udpClient);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Ошибка в работе приложения");
        }
    }

    private async Task HandleTcpClientAsync(TcpClient tcpClient)
    {
        string? userName = null; // Имя пользователя
        try
        {
            using var stream = tcpClient.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream);

            
            await writer.WriteLineAsync("Добро пожаловать в чат!");
            await writer.WriteLineAsync("Введите свое имя и нажмите Enter:");
            await writer.FlushAsync();

            // Получение имени пользователя
            userName = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = $"Гость_{Guid.NewGuid().ToString().Substring(0, 4)}"; // Присваиваем уникальное имя
            }

            Logger.Info($"{userName} подключился.");
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {userName} подключился.");
            await writer.WriteLineAsync($"Привет, {userName}! Вы подключены к чату.");
            await writer.WriteLineAsync("Чтобы выйти, просто закройте соединение.");
            await writer.WriteLineAsync("Для отправки личного сообщения используйте команду: @ [имя] [сообщение]");
            await writer.FlushAsync();

            // Добавляем клиента в список
            connectedClients[userName] = writer;

            // Уведомление о подключении
            BroadcastMessage($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {userName} подключился к чату.");
            Logger.Info($"Уведомление о подключении: {userName}");

            // Основной цикл обработки сообщений
            while (true)
            {
                string? message = await reader.ReadLineAsync();
                if (message == null) break;

                if (message.StartsWith("@"))
                {
                    // Обработка личного сообщения
                    var parts = message.Split(" ", 3);
                    if (parts.Length == 3)
                    {
                        string targetUser = parts[1];
                        string privateMessage = parts[2];
                        if (connectedClients.ContainsKey(targetUser))
                        {
                            await connectedClients[targetUser].WriteLineAsync($"[Личное сообщение от {userName}]: {privateMessage}");
                            await connectedClients[targetUser].FlushAsync();
                            Logger.Info($"Отправлено личное сообщение от {userName} к {targetUser}: {privateMessage}");
                        }
                        else
                        {
                            await writer.WriteLineAsync($"Пользователь {targetUser} не найден.");
                            await writer.FlushAsync();
                        }
                    }
                }
                else
                {
                    // Эхо-ответ клиенту
                    message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {userName}: {message}";
                    Console.WriteLine(message);
                    Logger.Info($"Сообщение от {userName}: {message}");

                    await writer.WriteLineAsync($"[Вы отправили]: {message}");
                    await writer.FlushAsync();

                    // Рассылка сообщения другим клиентам
                    BroadcastMessage(message);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Ошибка при обработке TCP клиента");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(userName))
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {userName} отключился.");
                Logger.Info($"{userName} отключился.");
                connectedClients.Remove(userName); // Удаляем клиента из списка

                BroadcastMessage($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {userName} отключился от чата.");
            }
            else
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Неизвестный клиент отключился.");
                Logger.Warn("Неизвестный клиент отключился.");
            }
        }
    }

    private void BroadcastMessage(string message)
    {
        foreach (var client in connectedClients.Values)
        {
            try
            {
                client.WriteLineAsync(message).Wait();
                client.FlushAsync().Wait();
            }
            catch
            {
                // Игнорируем ошибки отправки, если клиент отключился
            }
        }
    }

    private async Task SendMessageAsync(UdpClient udpClient)
    {
        Console.WriteLine("Введите сообщение для отправки (UDP):");
        Logger.Info("Запуск отправки сообщений через UDP.");
        while (true)
        {
            string? message = Console.ReadLine();
            if (string.IsNullOrEmpty(message)) continue;

            string timestampedMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
            byte[] data = System.Text.Encoding.UTF8.GetBytes(timestampedMessage);
            await udpClient.SendAsync(data, data.Length, host, udpPort);
            Logger.Info($"Отправлено UDP сообщение: {timestampedMessage}");
        }
    }


    private void LoadConfig()
    {
        try
        {
            string configContent = File.ReadAllText("config.json");
            var config = JsonSerializer.Deserialize<Config>(configContent);

            host = config?.Host ?? "127.0.0.1";
            tcpPort = config?.TcpPort ?? new Random().Next(8000, 9000);
            udpPort = config?.UdpPort ?? new Random().Next(9000, 10000);

            Console.WriteLine("Конфигурация загружена.");
            Logger.Info("Конфигурация загружена.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Ошибка загрузки конфигурации. Используются значения по умолчанию.");
            host = "127.0.0.1";
            tcpPort = new Random().Next(8000, 9000);
            udpPort = new Random().Next(9000, 10000);
        }
    }
    public void ChangeUdpPort(int newPort)
    {
        udpPort = newPort;
        Logger.Info($"Порт UDP изменен на {udpPort}");
        Console.WriteLine($"Порт UDP изменен на {udpPort}");
    }
    class Config
    {
        public string? Host { get; set; }
        public int? TcpPort { get; set; }
        public int? UdpPort { get; set; }
    }
}

class Program
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    static async Task Main(string[] args)
    {
        try
        {
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                Console.WriteLine("Завершение работы...");
                LogManager.Shutdown(); // Закрытие логгера при завершении
                Environment.Exit(0);
                Process.GetCurrentProcess().Kill();
            };

            var app = new ChatApplication();

            // Возможность изменить UDP порт
            Console.WriteLine("Хотите изменить UDP порт? (y/n)");
            if (Console.ReadLine()?.ToLower() == "y")
            {
                Console.WriteLine("Введите новый UDP порт:");
                if (int.TryParse(Console.ReadLine(), out int newPort))
                {
                    app.ChangeUdpPort(newPort);
                }
                else
                {
                    Console.WriteLine("Некорректный порт. Используется текущий.");
                }
            }

            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Ошибка при запуске приложения.");
            Console.WriteLine($"Ошибка: {ex.Message}");
        }
    }
}
