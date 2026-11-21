using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

public class UWPDumperXbox : Microsoft.Build.Utilities.Task
{
    public string? TargetName { get; set; }
    public string? Destination { get; set; }
    public bool? Overwrite { get; set; }
    public string? OverrideSource { get; set; }
    public uint? OverridePid { get; set; }

    public override bool Execute()
    {
        Util.Log = Log; // access Log from other classes

        if (TargetName == null || TargetName == "")
        {
            Util.LogError("Missing required parameter: TargetName");
            return false;
        }
        if (Destination == null || Destination == "")
        {
            Util.LogError("Missing required parameter: Destination");
            return false;
        }

        bool overwrite = Overwrite ?? false;

        uint pid;
        if (OverridePid != null)
        {
            pid = OverridePid.Value;
        }
        else if (!TryGetPid(TargetName, out pid))
        {
            return false;
        }
        Util.LogMessage($"Process ID: {pid}");

        using var remote = RemoteProcess.Open(pid);

        string exePath = remote.GetModuleFileNameW(IntPtr.Zero);
        Util.LogMessage($"Executable path: {exePath}");

        string sourcePath;
        if (OverrideSource == null || OverrideSource == "")
        {
            sourcePath = Path.GetDirectoryName(exePath)!;
        }
        else
        {
            sourcePath = OverrideSource;
        }
        sourcePath = Path.GetFullPath(sourcePath);
        Util.LogMessage($"Source path: {sourcePath}");

        if (Destination.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(Destination, UriKind.Absolute, out var uri))
            {
                Util.LogError($"Invalid URL: {Destination}");
                return false;
            }
            return UploadZipFile(remote, sourcePath, uri);
        }
        else
        {
            return WriteZipFile(remote, sourcePath, Destination, overwrite);
        }
    }

    static bool WriteZipFile(RemoteProcess remote, string sourcePath, string destination, bool overwrite)
    {
        destination = Path.GetFullPath(destination);
        string destPath;
        if (File.Exists(destination))
        {
            destPath = destination;
        }
        else if (Directory.Exists(destination))
        {
            destPath = Path.Combine(destination, Path.GetFileName(sourcePath) + ".zip");
        }
        else if (Directory.GetParent(destination)?.Exists ?? false)
        {
            destPath = destination;
        }
        else
        {
            Util.LogError($"Invalid destination: {destination}");
            return false;
        }
        if (File.Exists(destPath) && !overwrite)
        {
            Util.LogError($"File already exists (use -p:Overwrite=true to overwrite): {destination}");
            return false;
        }
        if (Directory.Exists(destPath))
        {
            Util.LogError($"Directory already exists: {destination}");
            return false;
        }
        Util.LogMessage($"Destination path: {destPath}");

        using var fileStream = new UnseekableFileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bufferedStream = new BufferedStream(fileStream, Util.SZ_8M);
        return WriteZipStream(remote, fileStream, sourcePath);
    }

    static bool UploadZipFile(RemoteProcess remote, string sourcePath, Uri destination)
    {
        using var client = new HttpClient();
        using var content = new ZipHttpContent(remote, sourcePath);
        using var form = new MultipartFormDataContent();
        var destName = Path.GetFileName(sourcePath) + ".zip";
        form.Add(content, "files", destName);
        var url = $"{destination.Scheme}://{destination.Host}:{destination.Port}/upload";
        Util.LogMessage($"Uploading {destName} to {url}");
        using var postTask = client.PostAsync(url, form);
        HttpResponseMessage? response = null;
        try
        {
            response = postTask.GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                Util.LogWarning($"HTTP POST failed: {response.StatusCode} {response.ReasonPhrase}");
            }
            return response.IsSuccessStatusCode && content.Success;
        }
        catch (HttpRequestException ex)
        {
            Util.LogError(ex.Message);
            return false;
        }
        finally
        {
            response?.Dispose();
        }
    }

    static bool WriteZipStream(RemoteProcess remote, Stream stream, string sourcePath)
    {
        // repeat warnings/errors at end 
        var warnings = new List<string>();
        var errors = new List<string>();
        var logWarning = new Action<string>(message =>
        {
            Util.LogWarning(message);
            warnings.Add(message);
        });
        var logError = new Action<string>(message =>
        {
            Util.LogError(message);
            errors.Add(message);
        });

        var readBuffer = new byte[RemoteProcess.BUFFER_SIZE - 4];
        var basePath = (Path.GetDirectoryName(sourcePath) ?? sourcePath.TrimEnd(Path.DirectorySeparatorChar)) + Path.DirectorySeparatorChar;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, true);
        Util.LogMessage("");
        Util.LogMessage("Files:");
        foreach (var record in remote.WalkDirectory(sourcePath))
        {
            var relativePath = record.Path.Substring(basePath.Length);
            if (record.IsDirectory)
            {
                relativePath += Path.DirectorySeparatorChar;
            }
            relativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            Util.LogMessage($"- {relativePath}");

            var entry = zip.CreateEntry(relativePath, CompressionLevel.Optimal);
            entry.LastWriteTime = DateTimeOffset.FromFileTime((long)record.ModifyTime);
            if (record.IsReparsePoint && !record.First)
            {
                var typeStr = record.IsDirectory ? "Directory" : "File";
                logWarning($"{typeStr} is reparse point: {record.Path}");
            }
            if (!record.IsDirectory)
            {
                var remaining = entry.Length;
                try
                {
                    using var hFile = remote.CreateFileW(record.Path, WinApi.GENERIC_READ, WinApi.FILE_SHARE_READ, WinApi.OPEN_EXISTING, WinApi.FILE_ATTRIBUTE_NORMAL);
                    using var entryStream = entry.Open();
                    while (true)
                    {
                        var readSize = remote.ReadFile(hFile, readBuffer);
                        if (readSize == 0)
                        {
                            break;
                        }
                        entryStream.Write(readBuffer, 0, (int)readSize);
                    }
                }
                catch (Exception ex)
                {
                    if (record.Size != 0)
                    {
                        logWarning(ex.Message);
                    }
                }
            }
        }

        if (warnings.Count > 0)
        {
            Util.LogMessage("");
            Util.LogMessage($"{warnings.Count} warnings occurred:");
            foreach (var message in warnings)
            {
                Util.LogMessage($"- {message}");
            }
        }
        if (errors.Count > 0)
        {
            Util.LogMessage("");
            Util.LogMessage($"{errors.Count} errors occurred:");
            foreach (var message in errors)
            {
                Util.LogMessage($"- {message}");
            }
        }
        return true;
    }

    public class ZipHttpContent : HttpContent
    {
        public bool Success { get; private set; }
        readonly RemoteProcess remote;
        readonly string sourcePath;
        public ZipHttpContent(RemoteProcess remote, string sourcePath)
        {
            this.remote = remote;
            this.sourcePath = sourcePath;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Success = WriteZipStream(remote, stream, sourcePath);
            return Task.CompletedTask;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = default;
            return false;
        }
    }

    // zip file written to disk doesn't 100% match zip file uploaded to server if the stream is seekable because of metadata differences
    public class UnseekableFileStream : FileStream
    {
        public UnseekableFileStream(string path, FileMode mode, FileAccess access, FileShare share)
            : base(path, mode, access, share)
        {

        }

        public override bool CanSeek => false;
    }

    static bool TryGetPid(string name, out uint pid)
    {
        if (Path.GetExtension(name).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.ChangeExtension(name, null);
        }

        var procs = Process.GetProcessesByName(name);
        if (procs.Length == 0)
        {
            Util.LogError($"Process not found: {name}");
            pid = default;
            return false;
        }
        if (procs.Length > 1)
        {
            Util.LogWarning($"{procs.Length} processes found: {name}");
        }
        pid = (uint)procs[0].Id;
        return true;
    }
}

public partial class RemoteProcess : IDisposable
{
    readonly SafeWaitHandle hProc;
    bool crashed = false;
    int lastError = 0;
    const int MAX_ARGS = 16;
    const int LAST_ERROR_OFFSET = 0;
    const int STOP_OFFSET = LAST_ERROR_OFFSET + 4;
    const int RETURN_VALUE_OFFSET = STOP_OFFSET + 4;
    const int FUNCTION_ADDRESS_OFFSET = RETURN_VALUE_OFFSET + 8;
    const int ARGS_OFFSET = FUNCTION_ADDRESS_OFFSET + 8;
    const int BUFFER_OFFSET = ARGS_OFFSET + 8 * MAX_ARGS;
    public const int BUFFER_SIZE = Util.SZ_8M;
    const int SHARED_MEMORY_SIZE = BUFFER_OFFSET + BUFFER_SIZE;
    readonly string sharedMemoryName;
    readonly string callEventName;
    readonly string returnEventName;
    readonly MemoryMappedFile sharedMemoryFile;
    readonly MemoryMappedViewAccessor sharedMemory;
    readonly EventWaitHandle callEvent;
    readonly EventWaitHandle returnEvent;
    readonly EventWaitHandle threadWait;
    readonly IntPtr pCode;
    readonly IntPtr sharedMemoryAddress;
    readonly IntPtr pBuffer;

    readonly IntPtr pCreateFileW;
    readonly IntPtr pReadFile;
    readonly IntPtr pCloseHandle;
    readonly IntPtr pGetModuleFileNameW;
    readonly IntPtr pGetFileAttributesExW;
    readonly IntPtr pFindFirstFileW;
    readonly IntPtr pFindNextFileW;
    readonly IntPtr pFindClose;

    public RemoteProcess(SafeWaitHandle hProc)
    {
        if (codeBytes.Length == 0)
        {
            throw new ArgumentException("codeBytes is empty");
        }

        this.hProc = hProc;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        sharedMemoryName = $"UWPDumper.SharedMemory.{timestamp}";
        callEventName = $"UWPDumper.Event.Call.{timestamp}";
        returnEventName = $"UWPDumper.Event.Return.{timestamp}";
        sharedMemoryFile = CreateSharedMemory(sharedMemoryName, SHARED_MEMORY_SIZE);
        sharedMemory = sharedMemoryFile.CreateViewAccessor();
        callEvent = CreateEvent(callEventName);
        returnEvent = CreateEvent(returnEventName);

        var hKernel32 = WinApi.GetModuleHandleW("kernel32.dll");
        var pGetProcAddress = WinApi.GetProcAddress(hKernel32, "GetProcAddress");

        var writer = new BinaryWriter();
        writer.Write(codeBytes);
        writer.Align(8);
        var codeArgOffset = writer.Size;
        writer.Write(hKernel32);
        writer.Write(pGetProcAddress);
        writer.Write(new IntPtr(SHARED_MEMORY_SIZE));
        writer.WriteStringW(sharedMemoryName, 64);
        writer.WriteStringW(callEventName, 64);
        writer.WriteStringW(returnEventName, 64);

        var codeSize = (ulong)writer.Size;
        pCode = WinApi.VirtualAllocEx(hProc, IntPtr.Zero, codeSize, WinApi.MEM_COMMIT | WinApi.MEM_RESERVE, WinApi.PAGE_READWRITE);
        var pCodeArg = pCode + codeArgOffset;
        WinApi.WriteProcessMemory(hProc, pCode, writer.Bytes.ToArray());
        WinApi.VirtualProtectEx(hProc, pCode, codeSize, WinApi.PAGE_EXECUTE_READ);

        var hThread = WinApi.CreateRemoteThread(hProc, IntPtr.Zero, 0, pCode, pCodeArg, 0, out _);
        threadWait = new(false, EventResetMode.AutoReset) { SafeWaitHandle = hThread };
        WaitForReturn(1000);
        sharedMemory.Read(RETURN_VALUE_OFFSET, out sharedMemoryAddress);
        pBuffer = sharedMemoryAddress + BUFFER_OFFSET;

        pCreateFileW = WinApi.GetProcAddress(hKernel32, "CreateFileW");
        pReadFile = WinApi.GetProcAddress(hKernel32, "ReadFile");
        pCloseHandle = WinApi.GetProcAddress(hKernel32, "CloseHandle");
        pGetModuleFileNameW = WinApi.GetProcAddress(hKernel32, "GetModuleFileNameW");
        pGetFileAttributesExW = WinApi.GetProcAddress(hKernel32, "GetFileAttributesExW");
        pFindFirstFileW = WinApi.GetProcAddress(hKernel32, "FindFirstFileW");
        pFindNextFileW = WinApi.GetProcAddress(hKernel32, "FindNextFileW");
        pFindClose = WinApi.GetProcAddress(hKernel32, "FindClose");
    }

    static EventWaitHandle CreateEvent(string name)
    {
        var attributes = CreateSecurityAttributes();
        try
        {
            var hEvent = WinApi.CreateEventW(attributes, false, false, name);
            return new(false, EventResetMode.AutoReset)
            {
                SafeWaitHandle = hEvent
            };
        }
        finally
        {
            WinApi.LocalFree(attributes.lpSecurityDescriptor);
        }
    }

    static MemoryMappedFile CreateSharedMemory(string name, ulong size)
    {
        var attributes = CreateSecurityAttributes();
        try
        {
            using var hFile = WinApi.CreateFileMappingW(new(WinApi.INVALID_HANDLE_VALUE, false), attributes, WinApi.PAGE_READWRITE, size, name);
            var sharedMemoryFile = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
            return sharedMemoryFile;
        }
        finally
        {
            WinApi.LocalFree(attributes.lpSecurityDescriptor);
        }
    }

    static WinApi.SECURITY_ATTRIBUTES CreateSecurityAttributes()
    {
        // DACL to allow full access to Everyone and AppContainers
        var descriptorString = "D:(A;;GA;;;WD)(A;;GA;;;AC)";
        WinApi.ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptorString, WinApi.SDDL_REVISION_1, out var descriptor, out _);
        return new()
        {
            nLength = WinApi.SECURITY_ATTRIBUTES.SIZE,
            lpSecurityDescriptor = descriptor,
            bInheritHandle = false,
        };
    }

    public static RemoteProcess Open(uint pid)
    {
        var access = WinApi.PROCESS_CREATE_THREAD | WinApi.PROCESS_VM_OPERATION | WinApi.PROCESS_VM_READ | WinApi.PROCESS_VM_WRITE | WinApi.PROCESS_QUERY_INFORMATION | WinApi.SYNCHRONIZE;
        var hProc = WinApi.OpenProcess(access, false, pid);
        return new RemoteProcess(hProc);
    }

    public bool Stop(int timeout = 1000)
    {
        sharedMemory.Write(STOP_OFFSET, true);
        callEvent.Set();
        if (timeout == 0)
        {
            return false;
        }
        return threadWait.WaitOne(timeout);
    }

    public void Dispose()
    {
        if (crashed)
        {
            Stop(0);
        }
        else if (Stop())
        {
            WinApi.VirtualFreeEx(hProc, pCode, 0, WinApi.MEM_RELEASE);
        }
        sharedMemory.Dispose();
        sharedMemoryFile.Dispose();
        callEvent.Dispose();
        returnEvent.Dispose();
        threadWait.Dispose();
        hProc.Dispose();
        GC.SuppressFinalize(this);
    }

    public IntPtr Call(IntPtr funcPtr, params Argument[] args)
    {
        if (args.Length > MAX_ARGS)
        {
            throw new ArgumentException($"Call: too many arguments: {args.Length} > {MAX_ARGS}");
        }

        sharedMemory.Write(FUNCTION_ADDRESS_OFFSET, funcPtr.ToInt64());
        int bufferSize = 0;
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var argOffset = ARGS_OFFSET + i * IntPtr.Size;
            if (arg.Scalar)
            {
                sharedMemory.Write(argOffset, (long)0);
                sharedMemory.WriteArray(argOffset, arg.Buffer, 0, arg.Buffer.Length);
            }
            else
            {
                var pArg = pBuffer + bufferSize;
                arg.SharedMemoryOffset = BUFFER_OFFSET + bufferSize;
                bufferSize += arg.Buffer.Length;
                if (bufferSize > BUFFER_SIZE)
                {
                    throw new ArgumentException($"Argument buffer too large: {bufferSize} > {BUFFER_SIZE}");
                }
                sharedMemory.Write(argOffset, pArg.ToInt64());
                if (arg.In)
                {
                    sharedMemory.WriteArray(arg.SharedMemoryOffset, arg.Buffer, 0, arg.Buffer.Length);
                }
            }
        }

        callEvent.Set();
        WaitForReturn(10000);

        lastError = sharedMemory.ReadInt32(LAST_ERROR_OFFSET);
        sharedMemory.Read(RETURN_VALUE_OFFSET, out IntPtr returnValue);
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Out && !arg.Scalar)
            {
                sharedMemory.ReadArray(arg.SharedMemoryOffset, arg.Buffer, 0, arg.Size ?? arg.Buffer.Length);
            }
        }

        return returnValue;
    }

    void WaitForReturn(int timeout)
    {
        var waitHandles = new[] { returnEvent, threadWait };
        var waitResult = WaitHandle.WaitAny(waitHandles, timeout);
        if (waitResult == WaitHandle.WaitTimeout)
        {
            crashed = true;
            throw new TimeoutException("remote thread timed out");
        }
        else if (waitHandles[waitResult] == threadWait)
        {
            crashed = true;
            var exitCode = WinApi.GetExitCodeThread(threadWait.SafeWaitHandle);
            throw new Exception($"remote thread terminated: {exitCode}");
        }
    }

    public RemoteHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, uint dwCreationDisposition, uint dwFlagsAndAttributes)
    {
        return CreateFileW(lpFileName, dwDesiredAccess, dwShareMode, IntPtr.Zero, dwCreationDisposition, dwFlagsAndAttributes, IntPtr.Zero);
    }

    public RemoteHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile)
    {
        var hFile = Call(pCreateFileW, Util.GetExtendedPath(lpFileName), dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
        if (hFile == WinApi.INVALID_HANDLE_VALUE)
        {
            throw new Win32Exception(GetLastError(), $"Remote CreateFileW(\"{lpFileName}\") failed: {GetLastErrorString()}");
        }
        return new(this, hFile);
    }

    public void CloseHandle(IntPtr hObject)
    {
        var success = (IntPtr.Zero != Call(pCloseHandle, hObject));
        if (!success)
        {
            throw new Win32Exception(GetLastError(), $"Remote CloseHandle failed: {GetLastErrorString()}");
        }
    }

    public uint ReadFile(RemoteHandle hFile, byte[] lpBuffer)
    {
        return ReadFile(hFile, lpBuffer, IntPtr.Zero);
    }

    public uint ReadFile(RemoteHandle hFile, byte[] lpBuffer, IntPtr lpOverlapped)
    {
        var lpNumberOfBytesRead = new byte[4];
        var buffer = new Argument(lpBuffer);
        var success = (IntPtr.Zero != Call(pReadFile, hFile, buffer, lpBuffer.Length, new(lpNumberOfBytesRead, Out: true), lpOverlapped));
        if (!success)
        {
            throw new Win32Exception(GetLastError(), $"Remote ReadFile failed: {GetLastErrorString()}");
        }
        var numberOfBytesRead = BitConverter.ToUInt32(lpNumberOfBytesRead, 0);
        sharedMemory.ReadArray(buffer.SharedMemoryOffset, buffer.Buffer, 0, (int)numberOfBytesRead);
        return numberOfBytesRead;
    }

    public string GetModuleFileNameW(IntPtr hModule)
    {
        var increment = WinApi.MAX_PATH * 2;
        var buffer = new byte[increment];
        while (true)
        {
            var size = (uint)Call(pGetModuleFileNameW, hModule, new(buffer, Out: true), buffer.Length / 2);
            if (GetLastError() == 0)
            {
                return Encoding.Unicode.GetString(buffer, 0, (int)size * 2);
            }
            else if (GetLastError() == WinApi.ERROR_INSUFFICIENT_BUFFER)
            {
                buffer = new byte[buffer.Length + increment];
            }
            else
            {
                throw new Win32Exception(GetLastError(), $"Remote GetModuleFileNameW failed: {GetLastErrorString()}");
            }
        }
    }

    public WinApi.WIN32_FILE_ATTRIBUTE_DATA GetFileAttributesExW(string lpFileName)
    {
        var buffer = new byte[WinApi.WIN32_FILE_ATTRIBUTE_DATA.SIZE];
        var success = (IntPtr.Zero != Call(pGetFileAttributesExW, Util.GetExtendedPath(lpFileName), (int)WinApi.GET_FILEEX_INFO_LEVELS.GetFileExInfoStandard, new(buffer, Out: true)));
        if (!success)
        {
            throw new Win32Exception(GetLastError(), $"Remote GetFileAttributesExW(\"{lpFileName}\") failed: {GetLastErrorString()}");
        }
        return new(buffer);
    }

    public RemoteFindHandle FindFirstFileW(string lpFileName, out WinApi.WIN32_FIND_DATAW lpFindFileData)
    {
        var buffer = new byte[WinApi.WIN32_FIND_DATAW.SIZE];
        var hFindFile = Call(pFindFirstFileW, Util.GetExtendedPath(lpFileName), new(buffer, Out: true));
        if (hFindFile == WinApi.INVALID_HANDLE_VALUE)
        {
            throw new Win32Exception(GetLastError(), $"Remote FindFirstFileW(\"{lpFileName}\") failed: {GetLastErrorString()}");
        }
        lpFindFileData = new WinApi.WIN32_FIND_DATAW(buffer);
        return new(this, hFindFile);
    }

    public void FindClose(IntPtr hFindFile)
    {
        var success = (IntPtr.Zero != Call(pFindClose, hFindFile));
        if (!success)
        {
            throw new Win32Exception(GetLastError(), $"Remote FindClose failed: {GetLastErrorString()}");
        }
    }

    public WinApi.WIN32_FIND_DATAW? FindNextFileW(RemoteFindHandle hFindFile)
    {
        var buffer = new byte[WinApi.WIN32_FIND_DATAW.SIZE];
        var success = (IntPtr.Zero != Call(pFindNextFileW, hFindFile, new(buffer, Out: true)));
        if (!success)
        {
            if (GetLastError() == WinApi.ERROR_NO_MORE_FILES)
            {
                return null;
            }
            throw new Win32Exception(GetLastError(), $"Remote FindNextFileW failed: {GetLastErrorString()}");
        }
        return new(buffer);
    }

    public int GetLastError()
    {
        return lastError;
    }

    public string GetLastErrorString()
    {
        return new Win32Exception(GetLastError()).Message;
    }

    public class RemoteHandle : IDisposable
    {
        public IntPtr Handle { get; protected set; }
        protected RemoteProcess remote;
        public RemoteHandle(RemoteProcess proc, IntPtr handle)
        {
            Handle = handle;
            this.remote = proc;
        }
        public void Dispose()
        {
            if (!remote.crashed && Handle != WinApi.INVALID_HANDLE_VALUE)
            {
                Close();
            }
            GC.SuppressFinalize(this);
        }
        public void Close()
        {
            remote.CloseHandle(Handle);
            Handle = WinApi.INVALID_HANDLE_VALUE;
        }
    }

    public class RemoteFindHandle : IDisposable
    {
        public IntPtr Handle { get; protected set; }
        protected RemoteProcess remote;
        public RemoteFindHandle(RemoteProcess proc, IntPtr handle)
        {
            Handle = handle;
            this.remote = proc;
        }
        public void Dispose()
        {
            if (!remote.crashed && Handle != WinApi.INVALID_HANDLE_VALUE)
            {
                Close();
            }
            GC.SuppressFinalize(this);
        }
        public void Close()
        {
            remote.FindClose(Handle);
            Handle = WinApi.INVALID_HANDLE_VALUE;
        }
    }

    public class Argument
    {
        public byte[] Buffer { get; }
        public int SharedMemoryOffset { get; set; }
        public int? Size { get; set; }
        public bool Scalar { get; set; }
        public bool In { get; set; }
        public bool Out { get; set; }
        public Argument(byte[] data, bool In = false, bool Out = false)
        {
            Buffer = data;
            this.In = In;
            this.Out = Out;
        }

        public static implicit operator Argument(bool v) => new(BitConverter.GetBytes(v)) { Scalar = true };
        public static implicit operator Argument(char v) => new(BitConverter.GetBytes(v)) { Scalar = true };
        public static implicit operator Argument(sbyte v) => new(BitConverter.GetBytes(v)) { Scalar = true };
        public static implicit operator Argument(byte v) => new(BitConverter.GetBytes(v)) { Scalar = true };
        public static implicit operator Argument(short v) => new(BitConverter.GetBytes(v)) { Scalar = true };
        public static implicit operator Argument(ushort v) => new(BitConverter.GetBytes(v)) { Scalar = true };
        public static implicit operator Argument(int v) => new(BitConverter.GetBytes(v)) { Scalar = true };
        public static implicit operator Argument(uint v) => new(BitConverter.GetBytes(v)) { Scalar = true };
        public static implicit operator Argument(long v) => new(BitConverter.GetBytes(v)) { Scalar = true };
        public static implicit operator Argument(ulong v) => new(BitConverter.GetBytes(v)) { Scalar = true };
        public static implicit operator Argument(IntPtr v) => v.ToInt64();
        public static implicit operator Argument(UIntPtr v) => v.ToUInt64();
        public static implicit operator Argument(RemoteHandle v) => v.Handle;
        public static implicit operator Argument(RemoteFindHandle v) => v.Handle;
        public static implicit operator Argument(string v)
        {
            var buffer = new byte[(v.Length + 1) * 2]; // add null terminator
            Encoding.Unicode.GetBytes(v, 0, v.Length, buffer, 0);
            return buffer;
        }

        public static implicit operator Argument(byte[] v) => new(v, In: true);
    }

    public class FileRecord
    {
        public string Path { get; }
        public bool IsDirectory { get; }
        public bool IsReparsePoint { get; }
        public ulong Size { get; }
        public ulong ModifyTime { get; }
        public bool First { get; } // first file in WalkDirectory

        public FileRecord(string path, bool isDirectory, ulong size, ulong modifyTime)
        {
            Path = path;
            IsDirectory = isDirectory;
            Size = size;
            ModifyTime = modifyTime;
        }

        public FileRecord(in WinApi.WIN32_FILE_ATTRIBUTE_DATA attributes, List<string> pathList, bool first = false)
        {
            Path = string.Join(System.IO.Path.DirectorySeparatorChar.ToString(), pathList);
            IsDirectory = attributes.IsDirectory();
            Size = attributes.GetFileSize();
            ModifyTime = attributes.ftLastWriteTime.ToULong();
            IsReparsePoint = attributes.IsReparsePoint();
            First = first;
        }

        public FileRecord(in WinApi.WIN32_FIND_DATAW findData, List<string> parents, bool first = false)
            : this(findData as WinApi.WIN32_FILE_ATTRIBUTE_DATA, parents, first)
        {
            Path = System.IO.Path.Combine(Path, findData.cFileName);
        }
    }

    public IEnumerable<FileRecord> WalkDirectory(string path)
    {
        var pathList = path.Split(Path.DirectorySeparatorChar).ToList();
        var attributes = GetFileAttributesExW(path);
        yield return new FileRecord(attributes, pathList, true);
        if (attributes.IsDirectory())
        {
            foreach (var record in WalkDirectoryRec(pathList))
            {
                yield return record;
            }
        }
    }
    IEnumerable<FileRecord> WalkDirectoryRec(List<string> pathList)
    {
        pathList.Add("*");
        var glob = string.Join(Path.DirectorySeparatorChar.ToString(), pathList);
        pathList.RemoveAt(pathList.Count - 1);

        using var hFindFile = FindFirstFileW(glob, out var findData);
        while (findData != null)
        {
            if (findData.cFileName != "." && findData.cFileName != "..")
            {
                yield return new FileRecord(findData, pathList);
                if (findData.IsDirectory() && !findData.IsReparsePoint())
                {
                    pathList.Add(findData.cFileName);
                    foreach (var record in WalkDirectoryRec(pathList))
                    {
                        yield return record;
                    }
                    pathList.RemoveAt(pathList.Count - 1);
                }
            }
            findData = FindNextFileW(hFindFile);
        }
    }
}

public static class WinApi
{
    static class Imports
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeWaitHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeWaitHandle CreateRemoteThread(SafeWaitHandle hProcess, IntPtr lpThreadAttributes, ulong dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetExitCodeThread(SafeWaitHandle hThread, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr VirtualAllocEx(SafeWaitHandle hProcess, IntPtr lpAddress, ulong dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool VirtualFreeEx(SafeWaitHandle hProcess, IntPtr lpAddress, ulong dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool VirtualProtectEx(SafeWaitHandle hProcess, IntPtr lpAddress, ulong dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool WriteProcessMemory(SafeWaitHandle hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, ulong nSize, out ulong lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool ReadProcessMemory(SafeWaitHandle hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, ulong nSize, out ulong lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeWaitHandle CreateEventW(in SECURITY_ATTRIBUTES lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeMemoryMappedFileHandle CreateFileMappingW(SafeFileHandle hFile, in SECURITY_ATTRIBUTES lpFileMappingAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string StringSecurityDescriptor, uint StringSDRevision, out IntPtr SecurityDescriptor, out IntPtr SecurityDescriptorSize);
    }

    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x4;
    public const uint PAGE_EXECUTE_READ = 0x20;
    public const uint GENERIC_ALL = 0x10000000;
    public const uint GENERIC_EXECUTE = 0x20000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint GENERIC_READ = 0x80000000;
    public const uint CREATE_NEW = 1;
    public const uint CREATE_ALWAYS = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint FILE_SHARE_DELETE = 4;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_ARCHIVE = 0x20;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x2000000;
    public const uint FILE_NAME_NORMALIZED = 0;
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;
    public const uint WAIT_FAILED = 0xFFFFFFFF;
    public const uint MAXIMUM_WAIT_OBJECTS = 64;
    public const uint PROCESS_CREATE_THREAD = 0x2;
    public const uint PROCESS_VM_OPERATION = 0x8;
    public const uint PROCESS_VM_READ = 0x10;
    public const uint PROCESS_VM_WRITE = 0x20;
    public const uint PROCESS_QUERY_INFORMATION = 0x400;
    public const uint SYNCHRONIZE = 0x100000;
    public const int MAX_PATH = 260;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int SDDL_REVISION_1 = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public const int SIZE = 0x18;

        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    public enum GET_FILEEX_INFO_LEVELS
    {
        GetFileExInfoStandard,
        GetFileExMaxInfoLevel,
    }

    public class FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public FILETIME(BinaryReader br)
        {
            dwLowDateTime = br.ReadU32();
            dwHighDateTime = br.ReadU32();
        }
        public FILETIME(byte[] data, int start = 0)
            : this(new(data, start))
        {

        }

        public ulong ToULong()
        {
            return ((ulong)dwHighDateTime << 32) | dwLowDateTime;
        }
    }

    public class WIN32_FILE_ATTRIBUTE_DATA
    {
        public const int SIZE = 0x24;

        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;

        public WIN32_FILE_ATTRIBUTE_DATA(BinaryReader br)
        {
            dwFileAttributes = br.ReadU32();
            ftCreationTime = new(br);
            ftLastAccessTime = new(br);
            ftLastWriteTime = new(br);
            nFileSizeHigh = br.ReadU32();
            nFileSizeLow = br.ReadU32();
        }

        public WIN32_FILE_ATTRIBUTE_DATA(byte[] data, int start = 0)
            : this(new(data, start))
        {

        }

        public bool IsDirectory()
        {
            return (dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        }

        public ulong GetFileSize()
        {
            return ((ulong)nFileSizeHigh << 32) | (ulong)nFileSizeLow;
        }

        public bool IsReparsePoint()
        {
            return (dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        }
    }

    public class WIN32_FIND_DATAW : WIN32_FILE_ATTRIBUTE_DATA
    {
        public new const int SIZE = 0x250;

        public uint dwReserved0;
        public uint dwReserved1;
        public string cFileName;
        public string cAlternateFileName;

        public WIN32_FIND_DATAW(BinaryReader br) : base(br)
        {
            dwReserved0 = br.ReadU32();
            dwReserved1 = br.ReadU32();
            cFileName = br.ReadStringW(MAX_PATH);
            cAlternateFileName = br.ReadStringW(14);
        }

        public WIN32_FIND_DATAW(byte[] data, int start = 0)
            : this(new(data, start))
        {

        }
    }

    public static int GetLastError()
    {
        return Marshal.GetLastWin32Error();
    }

    public static string GetLastErrorString()
    {
        return new Win32Exception(GetLastError()).Message;
    }

    public static IntPtr GetModuleHandleW(string lpModuleName)
    {
        var hMod = Imports.GetModuleHandleW(lpModuleName);
        if (hMod == IntPtr.Zero)
        {
            throw new Win32Exception(GetLastError(), $"GetModuleHandle failed: {lpModuleName}: {GetLastErrorString()}");
        }
        return hMod;
    }

    public static IntPtr GetProcAddress(IntPtr hModule, string lpProcName)
    {
        var proc = Imports.GetProcAddress(hModule, lpProcName);
        if (proc == IntPtr.Zero)
        {
            throw new Win32Exception(GetLastError(), $"GetProcAddress failed: {lpProcName}: {GetLastErrorString()}");
        }
        return proc;
    }

    public static SafeWaitHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId)
    {
        var hProc = Imports.OpenProcess(dwDesiredAccess, bInheritHandle, dwProcessId);
        if (hProc.IsInvalid)
        {
            throw new Win32Exception(GetLastError(), $"OpenProcess failed: {dwProcessId}: {GetLastErrorString()}");
        }
        return hProc;
    }

    public static void CloseHandle(IntPtr hObject)
    {
        var success = Imports.CloseHandle(hObject);
        if (!success)
        {
            throw new Win32Exception(GetLastError(), $"CloseHandle failed: {GetLastErrorString()}");
        }
    }

    public static SafeWaitHandle CreateRemoteThread(SafeWaitHandle hProcess, IntPtr lpThreadAttributes, ulong dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId)
    {
        var hThread = Imports.CreateRemoteThread(hProcess, lpThreadAttributes, dwStackSize, lpStartAddress, lpParameter, dwCreationFlags, out lpThreadId);
        if (hThread.IsInvalid)
        {
            throw new Win32Exception(GetLastError(), $"CreateRemoteThread failed: {GetLastErrorString()}");
        }
        return hThread;
    }

    public static uint GetExitCodeThread(SafeWaitHandle hThread)
    {
        if (!Imports.GetExitCodeThread(hThread, out uint lpExitCode))
        {
            throw new Win32Exception(GetLastError(), $"GetExitCodeThread failed: {GetLastErrorString()}");
        }
        return lpExitCode;
    }

    public static IntPtr VirtualAllocEx(SafeWaitHandle hProcess, IntPtr lpAddress, ulong dwSize, uint lpAllocationType, uint flProtect)
    {
        var mem = Imports.VirtualAllocEx(hProcess, lpAddress, dwSize, lpAllocationType, flProtect);
        if (mem == IntPtr.Zero)
        {
            throw new Win32Exception(GetLastError(), $"VirtualAllocEx failed: {GetLastErrorString()}");
        }
        return mem;
    }

    public static void VirtualFreeEx(SafeWaitHandle hProcess, IntPtr lpAddress, ulong dwSize, uint dwFreeType)
    {
        var success = Imports.VirtualFreeEx(hProcess, lpAddress, dwSize, dwFreeType);
        if (!success)
        {
            throw new Win32Exception(GetLastError(), $"VirtualFreeEx failed: {GetLastErrorString()}");
        }
    }

    public static uint VirtualProtectEx(SafeWaitHandle hProcess, IntPtr lpAddress, ulong dwSize, uint flNewProtect)
    {
        var success = Imports.VirtualProtectEx(hProcess, lpAddress, dwSize, flNewProtect, out uint lpflOldProtect);
        if (!success)
        {
            throw new Win32Exception(GetLastError(), $"VirtualProtectEx failed: {GetLastErrorString()}");
        }
        return lpflOldProtect;
    }

    public static void WriteProcessMemory(SafeWaitHandle hProcess, IntPtr lpBaseAddress, byte[] lpBuffer)
    {
        var nSize = (ulong)lpBuffer.Length;
        var success = Imports.WriteProcessMemory(hProcess, lpBaseAddress, lpBuffer, nSize, out ulong lpNumberOfBytesWritten);
        if (!success)
        {
            throw new Win32Exception(GetLastError(), $"WriteProcessMemory failed: {GetLastErrorString()}");
        }
        if (lpNumberOfBytesWritten != nSize)
        {
            throw new Exception($"WriteProcessMemory incomplete: {lpNumberOfBytesWritten} != {nSize}");
        }
    }

    public static void ReadProcessMemory(SafeWaitHandle hProcess, IntPtr lpBaseAddress, byte[] lpBuffer)
    {
        ulong nSize = (ulong)lpBuffer.Length;
        var success = Imports.ReadProcessMemory(hProcess, lpBaseAddress, lpBuffer, nSize, out ulong lpNumberOfBytesRead);
        if (!success)
        {
            throw new Win32Exception(GetLastError(), $"ReadProcessMemory failed: {GetLastErrorString()}");
        }
        if (lpNumberOfBytesRead != nSize)
        {
            throw new Exception($"ReadProcessMemory incomplete: {lpNumberOfBytesRead} != {nSize}");
        }
    }

    public static SafeWaitHandle CreateEventW(in SECURITY_ATTRIBUTES lpEventAttributes, bool bManualReset, bool bInitialState, string lpName)
    {
        var hEvent = Imports.CreateEventW(lpEventAttributes, bManualReset, bInitialState, lpName);
        if (hEvent.IsInvalid)
        {
            throw new Win32Exception(GetLastError(), $"CreateEventW failed: {GetLastErrorString()}");
        }
        return hEvent;
    }

    public static SafeMemoryMappedFileHandle CreateFileMappingW(SafeFileHandle hFile, in SECURITY_ATTRIBUTES lpFileMappingAttributes, uint flProtect, ulong dwMaximumSize, string lpName)
    {
        var hFileMapping = Imports.CreateFileMappingW(hFile, in lpFileMappingAttributes, flProtect, (uint)(dwMaximumSize >> 32), (uint)dwMaximumSize, lpName);
        if (hFileMapping.IsInvalid)
        {
            throw new Win32Exception(GetLastError(), $"CreateFileMappingW failed: {GetLastErrorString()}");
        }
        return hFileMapping;
    }


    public static void LocalFree(IntPtr hMem)
    {
        var success = (IntPtr.Zero == Imports.LocalFree(hMem));
        if (!success)
        {
            throw new Win32Exception(GetLastError(), $"LocalFree failed: {GetLastErrorString()}");
        }
    }

    public static void ConvertStringSecurityDescriptorToSecurityDescriptorW(string StringSecurityDescriptor, uint StringSDRevision, out IntPtr SecurityDescriptor, out IntPtr SecurityDescriptorSize)
    {
        var success = Imports.ConvertStringSecurityDescriptorToSecurityDescriptorW(StringSecurityDescriptor, StringSDRevision, out SecurityDescriptor, out SecurityDescriptorSize);
        if (!success)
        {
            throw new Win32Exception(GetLastError(), $"ConvertStringSecurityDescriptorToSecurityDescriptorW(\"{StringSecurityDescriptor}\") failed: {GetLastErrorString()}");
        }
    }
}

public class BinaryReader
{
    readonly byte[] bytes;
    int offset;
    public BinaryReader(byte[] bytes, int offset = 0)
    {
        this.bytes = bytes;
        this.offset = offset;
    }

    public uint ReadU32()
    {
        var value = BitConverter.ToUInt32(bytes, offset);
        offset += 4;
        return value;
    }

    public string ReadStringW(int count)
    {
        int byteCount = count * 2;
        var value = Encoding.Unicode.GetString(bytes, offset, byteCount);
        offset += byteCount;
        var endIndex = value.IndexOf("\0", StringComparison.Ordinal);
        if (endIndex != -1)
        {
            return value.Substring(0, endIndex);
        }
        return value;
    }
}

public class BinaryWriter
{
    public int Size { get => Bytes.Count; }
    public readonly List<byte> Bytes;

    public BinaryWriter()
    {
        Bytes = new();
    }

    public void Align(int alignment)
    {
        if (Bytes.Count % alignment == 0)
        {
            return;
        }
        var padding = alignment - (Bytes.Count % alignment);
        Write(new byte[padding]);
    }

    public void WriteStringW(string value, int? size)
    {
        if (size == null)
        {
            value += "\0";
        }
        else
        {
            if (value.Length + 1 > size)
            {
                throw new ArgumentException($"string too large: {value.Length + 1} > {size}");
            }
            value = value.PadRight(size.Value, '\0');
        }
        Write(Encoding.Unicode.GetBytes(value));
    }

    public void Write(IEnumerable<byte> value)
    {
        Bytes.AddRange(value);
    }

    public void Write(long value)
    {
        Write(BitConverter.GetBytes(value));
    }

    public void Write(IntPtr value)
    {
        Write(value.ToInt64());
    }
}

public static class Util
{
    public const int SZ_8M = 1024 * 1024 * 8;
    public const string ExtendedPathPrefix = @"\\?\";

    public static string GetExtendedPath(string path)
    {
        if (path.StartsWith(ExtendedPathPrefix))
        {
            return path;
        }
        return ExtendedPathPrefix + Path.GetFullPath(path);
    }

    public static Microsoft.Build.Utilities.TaskLoggingHelper? Log { get; set; }
    public static void LogMessage(string message, params object[] messageArgs)
    {
        Log?.LogMessage(Microsoft.Build.Framework.MessageImportance.High, message, messageArgs);
    }
    public static void LogWarning(string message, params object[] messageArgs)
    {
        Log?.LogWarning(message, messageArgs);
    }
    public static void LogError(string message, params object[] messageArgs)
    {
        Log?.LogError(message, messageArgs);
    }
}

public partial class RemoteProcess
{
    static readonly byte[] codeBytes = {
        // {{code_bytes}}
    };
}