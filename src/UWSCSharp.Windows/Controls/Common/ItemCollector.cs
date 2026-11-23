using System.Collections;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 一回の項目取得の結果と復帰後の更新禁止を管理します。
/// </summary>
internal sealed class ItemCollector(
    int maximumItems = UwscArray.MaximumElements,
    int maximumCharacters = NativeListControl.MaximumCharacters) : IReadOnlyList<string>, IList<string>, IDisposable
{
    private readonly object gate = new();
    private readonly List<string> items = [];
    private int characters;
    private bool frozen;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return items.Count;
            }
        }
    }

    public string this[int index]
    {
        get
        {
            lock (gate)
            {
                return items[index];
            }
        }
    }

    bool ICollection<string>.IsReadOnly => true;

    string IList<string>.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    void ICollection<string>.Add(string item) => throw new NotSupportedException();
    void ICollection<string>.Clear() => throw new NotSupportedException();
    bool ICollection<string>.Remove(string item) => throw new NotSupportedException();
    void IList<string>.Insert(int index, string item) => throw new NotSupportedException();
    void IList<string>.RemoveAt(int index) => throw new NotSupportedException();

    bool ICollection<string>.Contains(string item)
    {
        lock (gate)
        {
            return items.Contains(item);
        }
    }

    int IList<string>.IndexOf(string item)
    {
        lock (gate)
        {
            return items.IndexOf(item);
        }
    }

    void ICollection<string>.CopyTo(string[] array, int index)
    {
        lock (gate)
        {
            items.CopyTo(array, index);
        }
    }

    internal void Add(string text)
    {
        lock (gate)
        {
            if (frozen)
            {
                throw new OperationCanceledException("項目取得の結果は既に確定しています。");
            }

            if (items.Count >= maximumItems
                || text.Length > maximumCharacters - characters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("GETITEMの取得上限を超えました。"));
            }

            items.Add(text);
            characters += text.Length;
        }
    }

    public IEnumerator<string> GetEnumerator()
    {
        lock (gate)
        {
            // 列挙中の追加や次回取得に影響されない配列を渡します。
            string[] snapshot = [.. items];
            return ((IEnumerable<string>)snapshot).GetEnumerator();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        lock (gate)
        {
            frozen = true;
        }
    }
}
