using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

class Program
{
    #region Native J2534 Win32 API Imports

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string libname);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procedureName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PTOpen([MarshalAs(UnmanagedType.LPStr)] string pName, out uint pDeviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PTClose(uint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PTConnect(uint deviceId, uint protocolId, uint flags, uint baudRate, out uint pChannelId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PTDisconnect(uint channelId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PTStartPeriodicMsg(uint channelId, ref PASSTHRU_MSG pMsg, out uint pMsgId, uint timeInterval);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PTStopPeriodicMsg(uint channelId, uint msgId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PTStartMsgFilter(uint channelId, uint filterType, ref PASSTHRU_MSG pMask, ref PASSTHRU_MSG pPattern, IntPtr pFlowControl, out uint pFilterId);

    #endregion

    #region Structures and Constants

    public const uint CAN_PROTOCOL = 6;
    public const uint PASS_FILTER = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PASSTHRU_MSG
    {
        public uint ProtocolID;
        public uint RxStatus;
        public uint TxFlags;
        public uint Timestamp;
        public uint DataSize;
        public uint ExtraDataIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4128)]
        public byte[] Data;
    }

    public class J2534DeviceInfo
    {
        public string Name { get; set; }
        public string DllPath { get; set; }
    }

    #endregion

    private static readonly string LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");

    static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Exception ex = e.ExceptionObject as Exception;
            string fatalError = $"[КРИТИЧЕСКАЯ ОШИБКА СИСТЕМЫ] {ex?.ToString() ?? e.ExceptionObject.ToString()}";
            Log(fatalError, isError: true);
            Console.WriteLine("\nПриложение завершило работу с ошибкой. Нажмите Enter для выхода...");
            Console.ReadLine();
        };

        Log("=== Запуск приложения Lexus RX 200t Sonar Control ===");
        Log($"Путь запуска: {AppDomain.CurrentDomain.BaseDirectory}");
        Log($"Платформа (x86/x64): {(Environment.Is64BitProcess ? "x64" : "x86")}");

        try
        {
            RunApp();
        }
        catch (Exception ex)
        {
            Log($"[-] Исключение: {ex.Message}", isError: true);
            if (ex.InnerException != null)
            {
                Log($"[-] Внутреннее исключение: {ex.InnerException.Message}", isError: true);
            }
            Console.WriteLine("\nРабота завершена с ошибкой. Нажмите Enter для выхода...");
            Console.ReadLine();
        }
    }

    private static void RunApp()
    {
        J2534DeviceInfo device = FindJ2534Device("CHIPSOFT");
        Log($"[+] Найден адаптер в реестре: \"{device.Name}\"");
        Log($"[+] Путь к DLL: \"{device.DllPath}\"");

        IntPtr hModule = LoadLibrary(device.DllPath);
        if (hModule == IntPtr.Zero)
        {
            int winErr = Marshal.GetLastWin32Error();
            Log($"[-] Не удалось загрузить DLL. Win32 Error: {winErr}", isError: true);
            return;
        }

        var PassThruOpen = GetDelegate<PTOpen>(hModule, "PassThruOpen");
        var PassThruClose = GetDelegate<PTClose>(hModule, "PassThruClose");
        var PassThruConnect = GetDelegate<PTConnect>(hModule, "PassThruConnect");
        var PassThruDisconnect = GetDelegate<PTDisconnect>(hModule, "PassThruDisconnect");
        var PassThruStartPeriodicMsg = GetDelegate<PTStartPeriodicMsg>(hModule, "PassThruStartPeriodicMsg");
        var PassThruStopPeriodicMsg = GetDelegate<PTStopPeriodicMsg>(hModule, "PassThruStopPeriodicMsg");
        var PassThruStartMsgFilter = GetDelegate<PTStartMsgFilter>(hModule, "PassThruStartMsgFilter");

        uint deviceId = 0;
        uint channelId = 0;
        uint periodicMsgId = 0;
        uint filterId = 0;

        try
        {
            int status = PassThruOpen(device.Name, out deviceId);
            if (status != 0)
            {
                Log($"[*] Попытка открыть с передачей null в pName...", isError: false);
                status = PassThruOpen(null, out deviceId);
            }

            CheckStatus(status, "PassThruOpen");
            Log($"[+] Адаптер успешно открыт. Device ID: {deviceId}");

            status = PassThruConnect(deviceId, CAN_PROTOCOL, 0, 500000, out channelId);
            CheckStatus(status, "PassThruConnect");
            Log($"[+] Подключено к HS-CAN (500 kbps). Channel ID: {channelId}");

            // Настройка базового фильтра
            PASSTHRU_MSG mask = CreateCanMsg(0x000, new byte[8]);
            PASSTHRU_MSG pattern = CreateCanMsg(0x000, new byte[8]);
            PassThruStartMsgFilter(channelId, PASS_FILTER, ref mask, ref pattern, IntPtr.Zero, out filterId);

            // Начальное состояние — ON (0x01)
            byte currentStatusByte = 0x01;
            PASSTHRU_MSG sonarMsg = CreateCanMsg(0x399, new byte[] { currentStatusByte, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

            // Запускаем первоначальный периодический кадр (интервал 100 мс)
            status = PassThruStartPeriodicMsg(channelId, ref sonarMsg, out periodicMsgId, 100);
            CheckStatus(status, "PassThruStartPeriodicMsg");

            Console.Clear();
            PrintControlMenu();
            Log("[+] Интерактивный режим активирован. Текущий режим: ON (Зеленый)");

            bool isRunning = true;

            while (isRunning)
            {
                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo keyInfo = Console.ReadKey(intercept: true);

                    byte newStatusByte = currentStatusByte;
                    string modeName = "";

                    switch (keyInfo.Key)
                    {
                        case ConsoleKey.D1:
                        case ConsoleKey.NumPad1:
                            newStatusByte = 0x01;
                            modeName = "ON (Зелёный значок)";
                            break;

                        case ConsoleKey.D2:
                        case ConsoleKey.NumPad2:
                            newStatusByte = 0x00;
                            modeName = "OFF (Отключен)";
                            break;

                        case ConsoleKey.D3:
                        case ConsoleKey.NumPad3:
                            newStatusByte = 0x02;
                            modeName = "ERROR (Оранжевый / Ошибка сонаров)";
                            break;

                        case ConsoleKey.Escape:
                            isRunning = false;
                            continue;

                        default:
                            continue;
                    }

                    // Перезапускаем периодический кадр только если статус действительно изменился
                    if (newStatusByte != currentStatusByte || periodicMsgId == 0)
                    {
                        currentStatusByte = newStatusByte;

                        // Останавливаем старый периодический поток
                        if (periodicMsgId != 0)
                        {
                            PassThruStopPeriodicMsg(channelId, periodicMsgId);
                        }

                        // Собираем кадр с новым байтом состояния
                        PASSTHRU_MSG updatedMsg = CreateCanMsg(0x399, new byte[] { currentStatusByte, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

                        // Запускаем обновленный поток отправки
                        status = PassThruStartPeriodicMsg(channelId, ref updatedMsg, out periodicMsgId, 100);
                        if (status == 0)
                        {
                            Log($"[ВЫБОР] Режим изменен на: {modeName} [Байт 0: 0x{currentStatusByte:X2}]");
                        }
                        else
                        {
                            Log($"[-] Ошибка смены режима: Code {status}", isError: true);
                        }
                    }
                }

                Thread.Sleep(50); // Небольшая пауза для снижения нагрузки на ЦП
            }

            Log("\n[*] Остановка передачи кадра...");
            if (periodicMsgId != 0) PassThruStopPeriodicMsg(channelId, periodicMsgId);
        }
        finally
        {
            if (channelId != 0) PassThruDisconnect(channelId);
            if (deviceId != 0) PassThruClose(deviceId);
            Log("[+] Сессия J2534 корректно завершена.");
        }
    }

    private static void PrintControlMenu()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("      LEXUS RX 200t — PARK ASSIST / SONAR TEST     ");
        Console.WriteLine("==================================================");
        Console.WriteLine(" Управление значком парктроника (кадр 0x399):");
        Console.WriteLine("  [1] - Включить парктроник   (ON / Зелёный)");
        Console.WriteLine("  [2] - Выключить парктроник  (OFF / Погашен)");
        Console.WriteLine("  [3] - Вызвать ошибку        (ERROR / Оранжевый)");
        Console.WriteLine("  [Esc] - Завершить работу и выйти");
        Console.WriteLine("==================================================");
    }

    #region Logging & Error Handling

    private static void Log(string message, bool isError = false)
    {
        string formattedLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

        if (isError)
            Console.ForegroundColor = ConsoleColor.Red;

        Console.WriteLine(message);

        if (isError)
            Console.ResetColor();

        try
        {
            File.AppendAllText(LogFilePath, formattedLine + Environment.NewLine);
        }
        catch { /* Игнорируем ошибки записи файла */ }
    }

    private static void CheckStatus(int status, string actionName)
    {
        if (status != 0)
        {
            string description = GetJ2534ErrorDescription(status);
            throw new Exception($"{actionName} завершилась с ошибкой J2534 Code: {status} ({description})");
        }
    }

    private static string GetJ2534ErrorDescription(int code)
    {
        return code switch
        {
            0x01 => "ERR_NOT_SUPPORTED",
            0x02 => "ERR_INVALID_CHANNEL_ID",
            0x03 => "ERR_INVALID_PROTOCOL_ID",
            0x04 => "ERR_NULL_PARAMETER",
            0x05 => "ERR_CONFIG_VALUE",
            0x06 => "ERR_INVALID_DEVICE_ID",
            0x07 => "ERR_DEVICE_NOT_CONNECTED (Адаптер не подключен по USB или выключено зажигание)",
            0x08 => "ERR_TIMEOUT",
            0x09 => "ERR_MSG_PROTOCOL_MISMATCH",
            0x0A => "ERR_DEVICE_IN_USE (Адаптер занят другой программой)",
            _ => $"UNKNOWN_ERROR ({code})"
        };
    }

    #endregion

    #region Registry Search & Helpers

    private static J2534DeviceInfo FindJ2534Device(string vendorFilter = null)
    {
        string[] searchPaths = new string[]
        {
            @"SOFTWARE\WOW6432Node\PassThruSupport.04.04",
            @"SOFTWARE\PassThruSupport.04.04"
        };

        foreach (string basePath in searchPaths)
        {
            using (RegistryKey baseKey = Registry.LocalMachine.OpenSubKey(basePath))
            {
                if (baseKey == null) continue;

                foreach (string subkeyName in baseKey.GetSubKeyNames())
                {
                    if (!string.IsNullOrEmpty(vendorFilter) && !subkeyName.ToUpper().Contains(vendorFilter.ToUpper()))
                        continue;

                    using (RegistryKey deviceKey = baseKey.OpenSubKey(subkeyName))
                    {
                        if (deviceKey == null) continue;

                        string name = deviceKey.GetValue("Name") as string ?? subkeyName;
                        string dllPath = deviceKey.GetValue("FunctionLibrary") as string;

                        if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
                        {
                            return new J2534DeviceInfo { Name = name, DllPath = dllPath };
                        }
                    }
                }
            }
        }

        throw new Exception($"Адаптер {(vendorFilter != null ? $"(\"{vendorFilter}\")" : "")} не найден в реестре!");
    }

    private static T GetDelegate<T>(IntPtr module, string name) where T : Delegate
    {
        IntPtr procAddress = GetProcAddress(module, name);
        if (procAddress == IntPtr.Zero)
            throw new Exception($"Функция {name} не найдена в DLL.");
        return Marshal.GetDelegateForFunctionPointer<T>(procAddress);
    }

    private static PASSTHRU_MSG CreateCanMsg(uint canId, byte[] payload)
    {
        PASSTHRU_MSG msg = new PASSTHRU_MSG
        {
            ProtocolID = CAN_PROTOCOL,
            TxFlags = 0,
            DataSize = (uint)(4 + payload.Length),
            Data = new byte[4128]
        };

        msg.Data[0] = (byte)((canId >> 24) & 0xFF);
        msg.Data[1] = (byte)((canId >> 16) & 0xFF);
        msg.Data[2] = (byte)((canId >> 8) & 0xFF);
        msg.Data[3] = (byte)(canId & 0xFF);

        Array.Copy(payload, 0, msg.Data, 4, payload.Length);
        return msg;
    }

    #endregion
}