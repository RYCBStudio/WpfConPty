#pragma warning disable IDE0007 // 使用隐式类型
#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;
using Windows.Win32.System.Threading;

namespace RYCBEditorX.ConPty;

public sealed class ConPtySession : IDisposable
{
    private readonly SafeFileHandle _inputWriteSide;
    private readonly SafeFileHandle _outputReadSide;
    private readonly HPCON _hPCON;
    private readonly PROCESS_INFORMATION _processInfo;
    private readonly Thread _outputReaderThread;
    private bool _disposed;
    private IntPtr _lpAttributeList = IntPtr.Zero;

    public StreamWriter InputWriter
    {
        get;
    }
    public StreamReader OutputReader
    {
        get;
    }
    public event EventHandler<string> OutputReceived;

    public unsafe ConPtySession(string command, short width = 120, short height = 30)
    {
        // 创建通信管道
        CreatePipe(out _outputReadSide, out var outputWriteSide);
        CreatePipe(out var inputReadSide, out _inputWriteSide);

        // 创建伪控制台
        var size = new COORD { X = width, Y = height };
        PInvoke.CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out var hpc);
        _hPCON = (HPCON)hpc.DangerousGetHandle();



        // 准备进程启动参数
        var startupInfo = CreateStartupInfo(_hPCON);
        startupInfo.StartupInfo.hStdInput = (HANDLE)inputReadSide.DangerousGetHandle(); // 使用输入管道的读取端
        startupInfo.StartupInfo.hStdOutput = (HANDLE)outputWriteSide.DangerousGetHandle(); // 使用输出管道的写入端
        startupInfo.StartupInfo.hStdError = (HANDLE)outputWriteSide.DangerousGetHandle(); // 错误输出同标准输出
        startupInfo.StartupInfo.dwFlags |= STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES; // 确保标志已设置

        fixed (char* pCommandLine = command)
        {
            var commandLinePtr = (PCWSTR)pCommandLine;
            var cmdLnSpan = new Span<char>(pCommandLine, command.Length + 1);
            bool success = PInvoke.CreateProcess(
                lpApplicationName: null,
                lpCommandLine: ref cmdLnSpan,
                lpProcessAttributes: null,
                lpThreadAttributes: null,
                bInheritHandles: true,
                dwCreationFlags: PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW |
                               PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT,
                lpEnvironment: null,
                lpCurrentDirectory: null,
                lpStartupInfo: in startupInfo.StartupInfo,
                lpProcessInformation: out _processInfo
            );

            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        // 关闭不需要的管道端
        outputWriteSide.Dispose();
        inputReadSide.Dispose();

        //初始化IO流
        // 替换原有的流初始化代码
        InputWriter = new StreamWriter(
            new FileStream(_inputWriteSide, FileAccess.Write, 4096, false),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), // 禁用BOM
            4096,
            leaveOpen: false)
        {
            AutoFlush = true
        };

        var outputStream = new FileStream(_outputReadSide, FileAccess.Read, 4096, false);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        OutputReader = new StreamReader(outputStream,
             Encoding.GetEncoding("gb2312"), // 默认为GB2312
            detectEncodingFromByteOrderMarks: true, // 自动检测UTF-8
            leaveOpen: false
            );


        // 启动输出读取线程
        _outputReaderThread = new Thread(ReadOutputLoop);
        _outputReaderThread.IsBackground = true;
        _outputReaderThread.Start();
    }

    public async Task EnsureUTF8EncodingAsync()
    {
        await SendCommandAsync("chcp");
        string response = await ReadOutputAsync();
        if (!response.Contains("65001"))
        {
            await SendCommandAsync("chcp 65001 > nul");
        }
    }

    // 专门的方法发送命令
    public async Task SendCommandAsync(string command)
    {
        await InputWriter.WriteLineAsync(command);
        await InputWriter.FlushAsync(); // 双重确保
    }

    // 专门的方法读取输出
    public async Task<string> ReadOutputAsync()
    {
        var buffer = new char[1024];
        int read = await OutputReader.ReadAsync(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    private void CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe)
    {
        var sa = new Windows.Win32.Security.SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<Windows.Win32.Security.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        if (!PInvoke.CreatePipe(out readPipe, out writePipe, sa, 0))
        {
            throw new InvalidOperationException("Failed to create pipe.");
        }
    }
    private STARTUPINFOEXW CreateStartupInfo(HPCON hpc)
    {
        // 强制刷新错误状态
        Marshal.GetLastWin32Error();

        var startupInfo = new STARTUPINFOEXW
        {
            StartupInfo = new STARTUPINFOW
            {
                cb = (uint)Marshal.SizeOf<STARTUPINFOEXW>()
            }
        };

        // 1. 查询缓冲区大小（预期会失败并设置size）
        nuint size = 0;
        if (!InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 122) // 不是预期的缓冲区不足错误
            {
                throw new Win32Exception(err, $"初始化属性列表失败 (0x{err:X8})");
            }
        }

        // 2. 分配对齐的内存
        _lpAttributeList = Marshal.AllocHGlobal((int)size);
        ZeroMemory(_lpAttributeList, (int)size); // 清空内存

        // 3. 实际初始化
        if (!InitializeProcThreadAttributeList(_lpAttributeList, 1, 0, ref size))
        {
            int err = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(_lpAttributeList);
            throw new Win32Exception(err, $"创建属性列表失败 (0x{err:X8})");
        }

        // 4. 更新伪控制台属性（关键修复）
        IntPtr hpcValue = hpc.Value;
        if (!UpdateProcThreadAttribute(
            _lpAttributeList,
            0,
            0x00020016, // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
            ref hpcValue,
            (IntPtr)IntPtr.Size,
            IntPtr.Zero,
            IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            // 特殊处理：当err=0时可能是内存保护问题
            if (err == 0)
            {
                throw new AccessViolationException("内存访问冲突（可能因DEP保护）");
            }
            throw new Win32Exception(err, $"更新线程属性失败 (0x{err:X8})");
        }

        startupInfo.lpAttributeList = (LPPROC_THREAD_ATTRIBUTE_LIST)_lpAttributeList;
        return startupInfo;
    }

    // 内存清零辅助方法
    private static void ZeroMemory(IntPtr ptr, int size)
    {
        for (int i = 0; i < size; i++)
        {
            Marshal.WriteByte(ptr + i, 0);
        }
    }


    public void Resize(short width, short height)
    {
        var size = new COORD { X = width, Y = height };
        PInvoke.ResizePseudoConsole(_hPCON, size);
    }

    private void ReadOutputLoop()
    {
        var buffer = new char[1024];
        while (!_disposed)
        {
            try
            {
                int read = OutputReader.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    var text = new string(buffer, 0, read);
                    OutputReceived?.Invoke(this, text);
                }
            }
            catch (IOException ex)
            {
                if (!_disposed) Debug.WriteLine($"读取错误: {ex.Message}");
                break;
            }
            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        InputWriter?.Close();
        OutputReader?.Close();
        _inputWriteSide?.Close();
        _outputReadSide?.Close();

        if (_hPCON.IsNull)
        {
            PInvoke.ClosePseudoConsole(_hPCON);
        }

        // 清理进程
        PInvoke.CloseHandle(_processInfo.hProcess);
        PInvoke.CloseHandle(_processInfo.hThread);

        // 清理属性列表
        if (_lpAttributeList != IntPtr.Zero)
        {
            PInvoke.DeleteProcThreadAttributeList((Windows.Win32.System.Threading.LPPROC_THREAD_ATTRIBUTE_LIST)_lpAttributeList);
            Marshal.FreeHGlobal(_lpAttributeList);
            _lpAttributeList = IntPtr.Zero;
        }

        _outputReaderThread?.Join(500);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        uint dwFlags,
        ref nuint lpSize);

    [DllImport("api-ms-win-core-processthreads-l1-1-3.dll", ExactSpelling = true)]
    private static extern bool UpdateProcThreadAttribute(
    IntPtr lpAttributeList,
    uint dwFlags,
    IntPtr Attribute,
    ref IntPtr lpValue,
    IntPtr cbSize,
    IntPtr lpPreviousValue,
    IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);
}