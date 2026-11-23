using System.Runtime.InteropServices;
using System.Text.Json;

namespace UWSCSharp.ControlFixture;

internal static class ItemDataProbe
{
    private sealed record Observation(bool Combo, int Drawing, uint Data,
        byte Fill, long Result, string Bytes, string ScanUnits);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string name,
        string text, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window, uint message,
        nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    internal static void Run()
    {
        var observations = new List<Observation>();
        nint root = CreateWindowExW(0, "STATIC", "item data probe", 0,
            0, 0, 200, 200, 0, 0, 0, 0);
        if (root == 0)
        {
            throw new InvalidOperationException("probeの親windowを作成できません。");
        }

        try
        {
            foreach (bool combo in new[] { false, true })
            {
                foreach (int drawing in new[] { 0x10, 0x20 })
                {
                    nint control = CreateWindowExW(0, combo ? "COMBOBOX" : "LISTBOX", "",
                        0x40000000u | (uint)(drawing | (combo ? 3 : 0)),
                        0, 0, 100, 100, root, 0, 0, 0);
                    if (control == 0)
                    {
                        throw new InvalidOperationException("probeのcontrolを作成できません。");
                    }

                    foreach (uint data in new uint[] { 0, 0x41, 0x00420041, 0x00410000, 0xd83d })
                    {
                        nint index = SendMessageW(control, combo ? 0x143u : 0x180u, 0, (nint)data);
                        if (index < 0)
                        {
                            throw new InvalidOperationException("item dataの登録に失敗しました。");
                        }

                        foreach (byte fill in new byte[] { 0xcd, 0xef })
                        {
                            observations.Add(Read(control, combo, drawing, data, index, fill));
                        }
                    }
                }
            }
        }
        finally
        {
            DestroyWindow(root);
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            pointer_bytes = IntPtr.Size,
            runtime = Environment.Version.ToString(),
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            original_executed = false,
            observations
        }));
    }

    private static Observation Read(nint control, bool combo, int drawing,
        uint data, nint index, byte fill)
    {
        byte[] bytes = [.. Enumerable.Repeat(fill, 16)];
        // 終端はprobe側が末尾だけに置き、境界内の走査を保証します。
        bytes[^2] = 0;
        bytes[^1] = 0;
        nint buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            nint result = SendMessageW(control, combo ? 0x148u : 0x189u, index, buffer);
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            var units = new List<string>();
            for (int offset = 0; offset < bytes.Length; offset += 2)
            {
                int unit = bytes[offset] | bytes[offset + 1] << 8;
                if (unit == 0)
                {
                    break;
                }

                units.Add(unit.ToString("X4"));
            }

            return new Observation(combo, drawing, data, fill, result,
                Convert.ToHexString(bytes), string.Join(" ", units));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
