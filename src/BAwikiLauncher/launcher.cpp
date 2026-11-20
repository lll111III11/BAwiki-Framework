#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winternl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <string>
#include <vector>
#include <cstdio>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")

static std::wstring g_dir;
static std::wstring g_log;
static bool g_admin = false;
static bool g_elevatedAttempt = false;

static void Out(const wchar_t* s)
{
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    int n = WideCharToMultiByte(CP_UTF8, 0, s, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return;
    std::string buf(n - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s, -1, &buf[0], n, nullptr, nullptr);
    DWORD wrote = 0;
    WriteFile(h, buf.data(), (DWORD)buf.size(), &wrote, nullptr);
}

static void LogLine(const std::wstring& s)
{
    Out(s.c_str());
    Out(L"\r\n");
    HANDLE f = CreateFileW(g_log.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ,
                           nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    SYSTEMTIME st; GetLocalTime(&st);
    wchar_t ts[64];
    swprintf_s(ts, L"[%04d-%02d-%02d %02d:%02d:%02d] ", st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    std::wstring line = std::wstring(ts) + s + L"\r\n";
    int n = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), (int)line.size(), nullptr, 0, nullptr, nullptr);
    std::string buf(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, line.c_str(), (int)line.size(), &buf[0], n, nullptr, nullptr);
    DWORD wrote = 0;
    WriteFile(f, buf.data(), (DWORD)buf.size(), &wrote, nullptr);
    CloseHandle(f);
}

static void Pause(const wchar_t* msg)
{
    Out(L"\r\n");
    Out(msg);
    Out(L"\r\n  按任意键退出……");
    HANDLE h = GetStdHandle(STD_INPUT_HANDLE);
    DWORD mode = 0;
    if (GetConsoleMode(h, &mode))
    {
        SetConsoleMode(h, mode & ~ENABLE_LINE_INPUT & ~ENABLE_ECHO_INPUT);
        wchar_t c;
        DWORD read = 0;
        ReadConsoleW(h, &c, 1, &read, nullptr);
    }
    else
    {
        Sleep(60000);
    }
}

static std::wstring GetOsName()
{
    typedef LONG(WINAPI* RtlGetVersionFn)(PRTL_OSVERSIONINFOW);
    RTL_OSVERSIONINFOW vi = {};
    vi.dwOSVersionInfoSize = sizeof(vi);
    HMODULE nt = GetModuleHandleW(L"ntdll.dll");
    if (nt)
    {
        auto fn = (RtlGetVersionFn)GetProcAddress(nt, "RtlGetVersion");
        if (fn) fn(&vi);
    }
    wchar_t buf[160];
    swprintf_s(buf, L"Windows %u.%u.%u", vi.dwMajorVersion, vi.dwMinorVersion, vi.dwBuildNumber);
    return buf;
}

static bool DirWritable(const std::wstring& dir)
{
    std::wstring probe = dir + L"\\.bawiki_write_test";
    HANDLE h = CreateFileW(probe.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    CloseHandle(h);
    return true;
}

static bool FileExists(const std::wstring& p)
{
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

static bool DirExists(const std::wstring& p)
{
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

static std::wstring FindDataDir()
{
    std::vector<std::wstring> cands = {
        L"E:\\kivo_wiki", L"D:\\kivo_wiki", L"F:\\kivo_wiki",
        g_dir + L"\\..\\..\\kivo_wiki", g_dir + L"\\kivo_wiki"
    };
    for (auto& c : cands)
    {
        wchar_t full[MAX_PATH] = {};
        if (GetFullPathNameW(c.c_str(), MAX_PATH, full, nullptr))
        {
            std::wstring s = full;
            if (DirExists(s + L"\\01_学生")) return s;
        }
    }
    return L"";
}

static bool PortFree(int port)
{
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return true;
    sockaddr_in a = {};
    a.sin_family = AF_INET;
    a.sin_port = htons((u_short)port);
    inet_pton(AF_INET, "127.0.0.1", &a.sin_addr);
    bool ok = bind(s, (sockaddr*)&a, sizeof(a)) == 0;
    closesocket(s);
    return ok;
}

static bool HasVCRuntime()
{
    return GetModuleHandleW(L"vcruntime140.dll") != nullptr ||
           GetModuleHandleW(L"msvcp140.dll") != nullptr ||
           FileExists(L"C:\\Windows\\System32\\vcruntime140.dll");
}

static bool HasWebView2()
{
    HKEY k = nullptr;
    const wchar_t* path = L"SOFTWARE\\WOW6432Node\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, path, 0, KEY_READ, &k) == ERROR_SUCCESS)
    {
        RegCloseKey(k);
        return true;
    }
    return false;
}

static std::wstring FindDotNet()
{
    std::wstring base = L"C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App";
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((base + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return L"";
    std::wstring last;
    do
    {
        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && fd.cFileName[0] != L'.')
            last = fd.cFileName;
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return last;
}

static bool IsAdmin()
{
    BOOL ok = FALSE;
    PSID sid = nullptr;
    SID_IDENTIFIER_AUTHORITY nt = SECURITY_NT_AUTHORITY;
    if (AllocateAndInitializeSid(&nt, 2, SECURITY_BUILTIN_DOMAIN_RID,
                                 DOMAIN_ALIAS_RID_ADMINS, 0, 0, 0, 0, 0, 0, &sid))
    {
        CheckTokenMembership(nullptr, sid, &ok);
        FreeSid(sid);
    }
    return ok == TRUE;
}

static void WriteDiagnostics(const std::wstring& os, const std::wstring& dataDir,
                             const std::wstring& dotnet, bool vc, bool webview,
                             int port, bool admin, bool writable)
{
    std::wstring p = g_dir + L"\\BAwiki_diag.json";
    HANDLE f = CreateFileW(p.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    wchar_t buf[2048];
    SYSTEMTIME st; GetLocalTime(&st);
    wchar_t ts[64];
    swprintf_s(ts, L"%04d-%02d-%02d %02d:%02d:%02d", st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    swprintf_s(buf,
        L"{\n"
        L"  \"os\": \"%ls\",\n"
        L"  \"appDir\": \"%ls\",\n"
        L"  \"dataDir\": \"%ls\",\n"
        L"  \"dotnet\": \"%ls\",\n"
        L"  \"vcRuntime\": %ls,\n"
        L"  \"webview2\": %ls,\n"
        L"  \"freePort\": %d,\n"
        L"  \"admin\": %ls,\n"
        L"  \"appDirWritable\": %ls,\n"
        L"  \"time\": \"%ls\"\n}\n",
        os.c_str(), g_dir.c_str(), dataDir.c_str(), dotnet.c_str(),
        vc ? L"true" : L"false", webview ? L"true" : L"false", port,
        admin ? L"true" : L"false", writable ? L"true" : L"false", ts);
    int n = WideCharToMultiByte(CP_UTF8, 0, buf, -1, nullptr, 0, nullptr, nullptr);
    std::string out(n - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, buf, -1, &out[0], n, nullptr, nullptr);
    DWORD wrote = 0;
    WriteFile(f, out.data(), (DWORD)out.size(), &wrote, nullptr);
    CloseHandle(f);
}

static HANDLE g_child = nullptr;
static HANDLE g_outPipe = nullptr;

static DWORD WINAPI PumpThread(LPVOID)
{
    char buf[4096];
    DWORD read = 0;
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    while (ReadFile(g_outPipe, buf, sizeof(buf), &read, nullptr) && read > 0)
    {
        DWORD wrote = 0;
        WriteFile(hOut, buf, read, &wrote, nullptr);
        HANDLE f = CreateFileW(g_log.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ,
                               nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f != INVALID_HANDLE_VALUE)
        {
            WriteFile(f, buf, read, &wrote, nullptr);
            CloseHandle(f);
        }
    }
    return 0;
}

static int RunMain()
{

    wchar_t exe[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring exePath = exe;
    size_t slash = exePath.find_last_of(L"\\/");
    g_dir = slash == std::wstring::npos ? L"." : exePath.substr(0, slash);
    g_log = g_dir + L"\\launcher.log";
    DeleteFileW(g_log.c_str());
    SetConsoleOutputCP(CP_UTF8);
    g_admin = IsAdmin();

    Out(L"\r\n");
    Out(L"  ==================================================\r\n");
    Out(L"        BAwiki 原生启动器  ·  环境检测\r\n");
    Out(L"  ==================================================\r\n");

    std::wstring os = GetOsName();
    std::wstring dataDir = FindDataDir();
    std::wstring dotnet = FindDotNet();
    bool vc = HasVCRuntime();
    bool webview = HasWebView2();
    bool writable = DirWritable(g_dir);

    int port = 0;
    for (int p = 8234; p < 8260; p++) { if (PortFree(p)) { port = p; break; } }

    LogLine(L"[系统] " + os + L" / " + (sizeof(void*) == 8 ? L"64 位" : L"32 位"));
    LogLine(L"[权限] " + std::wstring(g_admin ? L"管理员" : L"普通用户"));
    LogLine(L"[目录] " + g_dir + (writable ? L"（可写）" : L"（不可写）"));
    LogLine(L"[数据] " + (dataDir.empty() ? L"未找到 kivo_wiki" : dataDir));
    LogLine(L"[依赖] .NET " + (dotnet.empty() ? L"未安装（自包含版可忽略）" : dotnet));
    LogLine(L"[依赖] VC++ 运行库 " + std::wstring(vc ? L"存在" : L"缺失"));
    LogLine(L"[依赖] WebView2 " + std::wstring(webview ? L"存在" : L"缺失（仅 Unity 查看器需要）"));
    LogLine(L"[端口] " + (port ? (L"可用 " + std::to_wstring(port)) : L"8234-8259 全部被占用"));

    WriteDiagnostics(os, dataDir, dotnet, vc, webview, port, g_admin, writable);

    bool critical = !writable || dataDir.empty() || port == 0;
    if (critical && !g_admin && !g_elevatedAttempt)
    {
        LogLine(L"[判定] 最低环境不完整，尝试以管理员权限重新启动……");
        g_elevatedAttempt = true;
        SHELLEXECUTEINFOW sei = {};
        sei.cbSize = sizeof(sei);
        sei.lpVerb = L"runas";
        sei.lpFile = exePath.c_str();
        sei.lpParameters = L"--elevated";
        sei.nShow = SW_SHOWNORMAL;
        if (ShellExecuteExW(&sei)) return 0;
        DWORD err = GetLastError();
        if (err == ERROR_CANCELLED)
            LogLine(L"[提权] 用户取消了 UAC 授权，继续以普通权限运行。");
        else
            LogLine(L"[提权] 无法申请管理员权限（错误 " + std::to_wstring(err) + L"），继续。");
    }

    std::wstring app = g_dir + L"\\BAwikiWeb.exe";
    if (!FileExists(app))
    {
        LogLine(L"[失败] 没有找到 BAwikiWeb.exe，请确认启动器与主程序在同一目录。");
        Pause(L"  启动失败。");
        return 2;
    }

    wchar_t cmd[512];
    swprintf_s(cmd, L"\"%ls\"", app.c_str());

    SECURITY_ATTRIBUTES sa = {};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;
    HANDLE rd = nullptr, wr = nullptr;
    CreatePipe(&rd, &wr, &sa, 0);
    SetHandleInformation(rd, HANDLE_FLAG_INHERIT, 0);
    g_outPipe = rd;

    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    si.hStdOutput = wr;
    si.hStdError = wr;

    PROCESS_INFORMATION pi = {};
    std::wstring cmdline = cmd;
    BOOL ok = CreateProcessW(app.c_str(), &cmdline[0], nullptr, nullptr, TRUE,
                             CREATE_NEW_PROCESS_GROUP, nullptr, g_dir.c_str(), &si, &pi);
    CloseHandle(wr);
    if (!ok)
    {
        LogLine(L"[失败] 无法启动主程序（错误 " + std::to_wstring(GetLastError()) + L"）");
        Pause(L"  启动失败。");
        return 3;
    }

    g_child = pi.hProcess;
    CloseHandle(pi.hThread);
    CreateThread(nullptr, 0, PumpThread, nullptr, 0, nullptr);

    DWORD wait = WaitForSingleObject(pi.hProcess, 8000);
    if (wait == WAIT_OBJECT_0)
    {
        DWORD code = 0;
        GetExitCodeProcess(pi.hProcess, &code);
        Sleep(300);
        LogLine(L"[退出] 主程序提前退出，退出码 " + std::to_wstring(code));
        LogLine(L"[诊断] 已写入 launcher.log 与 BAwiki_diag.json");
        if (!FileExists(g_dir + L"\\BAwiki_crash.log"))
            LogLine(L"[诊断] 未生成 BAwiki_crash.log：通常是缺少运行库或程序文件不完整。");
        Pause(L"  主程序没有成功启动，窗口已保留。");
        return (int)code;
    }

    LogLine(L"[运行] 主程序已启动，本窗口关闭即停止服务。");
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 0;
    GetExitCodeProcess(pi.hProcess, &code);
    LogLine(L"[退出] 主程序结束，退出码 " + std::to_wstring(code));
    return (int)code;
}

int wmain(int argc, wchar_t** argv)
{
    for (int i = 1; i < argc; i++)
    {
        if (wcscmp(argv[i], L"--uac-probe") == 0) return 0;
        if (wcscmp(argv[i], L"--elevated") == 0) g_elevatedAttempt = true;
    }

    int code = RunMain();
    if (code != 0)
    {

        Pause(L"  程序异常结束。");
    }
    return code;
}
