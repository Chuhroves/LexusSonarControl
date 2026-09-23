using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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

    static void Main(string[] args)
    {
        Console.WriteLine("=== Lexus RX 200t Sonar Control (Universal J2534) ===");

        J2534DeviceInfo device = null;

        try
        {
            // 1. Поиск адаптера CHIPSOFT (или любого доступного J2534) в реестре
            device = FindJ2534Device("CHIPSOFT");

            Console.WriteLine($"[+] Найден адаптер: \"{device.Name}\"");
            Console.WriteLine($"[+] Путь к DLL: \"{device.DllPath}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[-] Ошибка поиска в реестре: {ex.Message}");
            Console.ReadLine();
            return;
        }

        // 2. Загрузка найденной DLL
        IntPtr hModule = LoadLibrary(device.DllPath);
        if (hModule == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"[-] Не удалось загрузить DLL {device.DllPath}. Win32 Error: {err}");
            Console.ReadLine();
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
            // 3. Открываем устройство с передачей имени из реестра
            int status = PassThruOpen(device.Name, out deviceId);

            // Если не открылось по точному имени, делаем фоллбэк с передачей null
            if (status != 0)
            {
                status = PassThruOpen(null, out deviceId);
            }

            CheckStatus(status, "PassThruOpen");
            Console.WriteLine($"[+] Адаптер успешно открыт! Device ID: {deviceId}");

            // 4. Подключение к HS-CAN (500 kbps)
            status = PassThruConnect(deviceId, CAN_PROTOCOL, 0, 500000, out channelId);
            CheckStatus(status, "PassThruConnect");
            Console.WriteLine($"[+] Подключено к HS-CAN (500 kbps). Channel ID: {channelId}");

            // 5. Установка базового фильтра
            PASSTHRU_MSG mask = CreateCanMsg(0x000, new byte[8]);
            PASSTHRU_MSG pattern = CreateCanMsg(0x000, new byte[8]);
            PassThruStartMsgFilter(channelId, PASS_FILTER, ref mask, ref pattern, IntPtr.Zero, out filterId);

            // 6. Формирование кадра парктроника (CAN ID 0x399)
            byte[] activePayload = new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            PASSTHRU_MSG sonarMsg = CreateCanMsg(0x399, activePayload);

            // 7. Запуск фоновой отправки раз в 100 мс
            status = PassThruStartPeriodicMsg(channelId, ref sonarMsg, out periodicMsgId, 100);
            CheckStatus(status, "PassThruStartPeriodicMsg");

            Console.WriteLine("\n[УСПЕХ] Статус (ON / Зелёная иконка) отправляется в шину CAN.");
            Console.WriteLine("Нажмите Enter для завершения...");
            Console.ReadLine();

            PassThruStopPeriodicMsg(channelId, periodicMsgId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[-] Ошибка выполнения: {ex.Message}");
            Console.ReadLine();
        }
        finally
        {
            if (channelId != 0) PassThruDisconnect(channelId);
            if (deviceId != 0) PassThruClose(deviceId);
            Console.WriteLine("[+] Сессия завершена.");
        }
    }

    #region Registry Search Engine

    /// <summary>
    /// Ищет зарегистрированный J2534 адаптер в реестре Windows.
    /// </summary>
    /// <param name="vendorFilter">Фильтр по имени (например "CHIPSOFT"). Если null — вернет первый попавшийся J2534.</param>
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
                    // Проверка фильтра производителя
                    if (!string.IsNullOrEmpty(vendorFilter) && !subkeyName.ToUpper().Contains(vendorFilter.ToUpper()))
                    {
                        continue;
                    }

                    using (RegistryKey deviceKey = baseKey.OpenSubKey(subkeyName))
                    {
                        if (deviceKey == null) continue;

                        string name = deviceKey.GetValue("Name") as string ?? subkeyName;
                        string dllPath = deviceKey.GetValue("FunctionLibrary") as string;

                        if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
                        {
                            return new J2534DeviceInfo
                            {
                                Name = name,
                                DllPath = dllPath
                            };
                        }
                    }
                }
            }
        }

        throw new Exception($"Подходящий J2534-адаптер {(vendorFilter != null ? $"(\"{vendorFilter}\")" : "")} не найден в реестре Windows!");
    }

    #endregion

    #region Helpers

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

    private static void CheckStatus(int status, string actionName)
    {
        if (status != 0)
            throw new Exception($"{actionName} завершилась с ошибкой J2534 Code: {status}");
    }

    #endregion
}