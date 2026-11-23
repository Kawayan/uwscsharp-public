using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class WindowsShutdownPrivilegeAcceptanceTests(ITestOutputHelper output)
{
    private const int TokenPrivileges = 3;
    private const int Enabled = 2;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoresPrivilegeAfterOperation(bool operationFails)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var identity = WindowsIdentity.GetCurrent();
        SafeAccessTokenHandle token = identity.AccessToken;
        Assert.True(LookupPrivilegeValueW(null, "SeShutdownPrivilege",
            out long luid), $"LookupPrivilegeValueW: {Marshal.GetLastPInvokeError()}");
        int before = ReadAttributes(token, luid);
        int during = -1;
        var failure = new InvalidOperationException("検証用の操作失敗です。");

        void Operation()
        {
            during = ReadAttributes(token, luid);
            if (operationFails)
            {
                throw failure;
            }
        }

        if (operationFails)
        {
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                WindowsPowerOperations.WithShutdownPrivilege(Operation)));
        }
        else
        {
            WindowsPowerOperations.WithShutdownPrivilege(Operation);
        }

        int after = ReadAttributes(token, luid);
        output.WriteLine($"before={before}; during={during}; after={after}");
        Assert.Equal(0, before & Enabled);
        Assert.NotEqual(0, during & Enabled);
        Assert.NotEqual(-1, during);
        Assert.Equal(before, after);
    }

    private static int ReadAttributes(SafeAccessTokenHandle token, long luid)
    {
        GetTokenInformation(token, TokenPrivileges, 0, 0, out int length);
        Assert.True(length >= sizeof(int), "トークン特権のサイズを取得できません。");
        nint buffer = Marshal.AllocHGlobal(length);
        try
        {
            Assert.True(GetTokenInformation(token, TokenPrivileges,
                buffer, length, out _),
                $"GetTokenInformation: {Marshal.GetLastPInvokeError()}");
            int count = Marshal.ReadInt32(buffer);
            const int entrySize = sizeof(long) + sizeof(int);
            Assert.InRange(count, 0, (length - sizeof(int)) / entrySize);
            for (int i = 0; i < count; i++)
            {
                int offset = sizeof(int) + i * entrySize;
                if (Marshal.ReadInt64(buffer, offset) == luid)
                {
                    return Marshal.ReadInt32(buffer, offset + sizeof(long));
                }
            }
            throw new InvalidOperationException("終了特権が割り当てられていません。");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token, int informationClass, nint information,
        int length, out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(
        string? system, string name, out long luid);
}
