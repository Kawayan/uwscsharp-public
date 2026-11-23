using System.Diagnostics;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class UiaListActionNumberTests(UiaListActionFixture fixture)
    : IClassFixture<UiaListActionFixture>
{
    [Fact]
    public async Task FailedSharedProcessIsRemovedBeforeTheNextCase()
    {
        (Process process, _) = await fixture.GetAsync(false);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.GetAsync(false));
        (Process recovered, long handle) = await fixture.GetAsync(false);
        Assert.NotSame(process, recovered);
        Assert.NotEqual(0, handle);
    }

    [Theory]
    [InlineData(2, null, true)]
    [InlineData(2, 0, true)]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, true)]
    [InlineData(2, -1, false)]
    [InlineData(2, -2, true)]
    [InlineData(1073741826, null, true)]
    [InlineData(1073741826, 0, true)]
    [InlineData(1073741826, 1, false)]
    [InlineData(1073741826, 2, true)]
    [InlineData(1073741826, -1, false)]
    [InlineData(1073741826, -2, true)]
    public async Task ZeroOrOmittedListNumberSearchesEveryContainer(
        int flags, int? number, bool expected)
    {
        await Run(flags, number, "second", expected, 0, expected ? 1 : 0);
    }

    [Theory]
    [InlineData(16386, null, 1, 0)]
    [InlineData(16386, 0, 1, 0)]
    [InlineData(16386, 1, 1, 0)]
    [InlineData(16386, 2, 2, 0)]
    [InlineData(16386, 3, 0, 2)]
    [InlineData(16386, -3, 0, 2)]
    [InlineData(16386, 4, 0, 0)]
    [InlineData(1073758210, null, 1, 0)]
    [InlineData(1073758210, 0, 1, 0)]
    [InlineData(1073758210, 1, 1, 0)]
    [InlineData(1073758210, 2, 2, 0)]
    [InlineData(1073758210, 3, 0, 2)]
    [InlineData(1073758210, -3, 0, 2)]
    [InlineData(1073758210, 4, 0, 0)]
    public async Task OfThisCountsMatchingItemsAcrossListAndCombo(
        int flags, int? number, int list, int combo)
    {
        await Run(flags, number, "shared", number != 4, list, combo);
    }

    [Theory]
    [InlineData(2, "beta\tgamma", 1, 0, true, "beta,gamma")]
    [InlineData(1073741826, "beta\tgamma", 1, -1, true, "beta,gamma")]
    [InlineData(2, "beta\tmissing", 1, 1, true, "beta")]
    [InlineData(1073741826, "beta\tmissing", 0, 0, true, "alpha")]
    [InlineData(2, "beta\tgamma", 0, 0, true, "alpha")]
    [InlineData(2, "missing\tother", 1, 0, false, "alpha")]
    [InlineData(258, "bet\tgam", 1, 0, true, "beta,gamma")]
    [InlineData(2, "shared\tshared", 1, 0, true, "shared,shared")]
    [InlineData(2, "\tbeta\t", 1, 0, true, "beta")]
    [InlineData(2, "\t\t", 1, 0, false, "alpha")]
    [InlineData(16386, "beta\tgamma", 1, 2, true, "gamma")]
    [InlineData(16386, "beta\tgamma", 1, 3, false, "alpha")]
    [InlineData(1073741826 | 2048, "beta\tgamma", 1, 0, false, "alpha")]
    public async Task MultipleListNamesChangeTheSelectionSet(
        int flags, string caption, int request, int number, bool expected,
        string selection)
    {
        await Run(flags, number, caption, expected, 0, 0,
            multiple: true, request: request, selection: selection);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1073741826)]
    public async Task MultipleNamesDoNotSelectTheFirstTermInSingleSelectLists(int flags)
    {
        await Run(flags, 0, "alpha\tshared", false, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalClickDoesNotPreselectTheTargetThroughFocus(bool background)
    {
        await Run(1073741826 | 2048, 1, "beta", true, 0, 0,
            multiple: true, selection: "alpha", observeInput: true,
            background: background);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1073741824)]
    [InlineData(1073741828)]
    public async Task MissingApplicationItemDoesNotExpandTheSystemMenu(int flags)
    {
        await Run(flags, 1, "missing", false, 0, 0, verifyNoSystemMenu: true);
    }

    private async Task Run(int flags, int? number, string caption,
        bool expected, int list, int combo, bool multiple = false,
        int request = 1, string selection = "", bool observeInput = false,
        bool background = true, bool verifyNoSystemMenu = false)
    {
        bool reusable = !observeInput && !verifyNoSystemMenu;
        Process process;
        long handle;
        if (reusable)
        {
            (process, handle) = await fixture.GetAsync(multiple);
        }
        else
        {
            process = UiaListActionFixture.Start(multiple);
            handle = 0;
        }

        bool completed = false;
        try
        {
            if (!reusable)
            {
                string? ready = await process.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(long.TryParse(ready, out handle), process.HasExited
                    ? await process.StandardError.ReadToEndAsync() : ready);
            }
            string? pointerSnapshot = null;
            IWindowsAutomationSession session = new WindowsAutomation(input =>
                {
                    if (!observeInput)
                    {
                        throw new InvalidOperationException("物理入力は禁止です。");
                    }
                    if (input.Data.Mouse.Flags == 2)
                    {
                        process.StandardInput.WriteLine("snapshot");
                        pointerSnapshot = process.StandardOutput.ReadLineAsync()
                            .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    }
                })
                .CreateSession();
            await using (session)
            {
                Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric(handle)], default);
                List<Value> arguments = [id, Value.String(caption), Value.Numeric(flags | (background ? 512 : 0))];
                if (number.HasValue)
                {
                    arguments.Add(Value.Numeric(request));
                    arguments.Add(Value.Numeric(number.Value));
                }

                Assert.Equal(Value.Numeric(expected ? 1 : 0),
                    await session.InvokeAsync("CLKITEM", arguments, default)
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
                if (observeInput)
                {
                    Assert.EndsWith(";selected=alpha", pointerSnapshot);
                }
                if (verifyNoSystemMenu)
                {
                    await process.StandardInput.WriteLineAsync("menu-stats");
                    Assert.Equal("system-menu=0", await process.StandardOutput.ReadLineAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10)));
                }
                await process.StandardInput.WriteLineAsync("snapshot");
                string snapshot = multiple
                    ? $"list={list};combo={combo};expanded=False;selected={selection}"
                    : $"list={list};combo={combo};expanded=False";
                string? actual = await process.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                if (multiple)
                {
                    Assert.EndsWith($";combo={combo};expanded=False;selected={selection}", actual);
                }
                else
                {
                    Assert.Equal(snapshot, actual);
                }
                if (!reusable)
                {
                    await process.StandardInput.WriteLineAsync("exit");
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(0, process.ExitCode);
                }

            }

            completed = true;
        }
        finally
        {
            if (reusable)
            {
                if (!completed)
                {
                    await fixture.InvalidateAsync(process);
                }
            }
            else
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                process.Dispose();
            }
        }
    }
}
