using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace UWSCSharp.Core;

/// <summary>
/// 実行内で宣言を共有し、各関数の最新の登録を保持します。
/// </summary>
internal sealed class NativeDllRegistry
{
    private readonly record struct Entry(NativeDllDeclarationStatement Statement,
        bool Executed);

    private readonly ConcurrentDictionary<string, Entry>
        declarations = new(StringComparer.OrdinalIgnoreCase);
    private readonly NativeDllRegistry? parent;
    private readonly object publicationGate = new();
    private volatile bool published;

    internal NativeDllRegistry(IEnumerable<NativeDllDeclarationStatement> initial,
        NativeDllRegistry? parent = null)
    {
        this.parent = parent;
        foreach (NativeDllDeclarationStatement statement in initial)
        {
            string name = statement.Declaration.Name;
            if (parent is null || !parent.TryGet(name, out _))
            {
                declarations.TryAdd(name, new Entry(statement, false));
            }
        }
    }

    internal void Register(NativeDllDeclarationStatement statement)
    {
        if (parent is null)
        {
            declarations[statement.Declaration.Name] = new Entry(statement, true);
            return;
        }

        lock (publicationGate)
        {
            if (published)
            {
                parent.Register(statement);
            }
            else
            {
                declarations[statement.Declaration.Name] = new Entry(statement, true);
            }
        }
    }

    internal void Publish()
    {
        NativeDllRegistry target = parent
            ?? throw new InvalidOperationException("公開先の登録がありません。");
        lock (publicationGate)
        {
            foreach ((string name, Entry entry) in declarations)
            {
                if (entry.Executed)
                {
                    target.Register(entry.Statement);
                }
                else
                {
                    target.declarations.TryAdd(name, entry);
                }
            }

            published = true;
        }
    }

    internal bool TryGet(string name,
        [NotNullWhen(true)] out NativeDllDeclarationStatement? declaration)
    {
        if (!published && declarations.TryGetValue(name, out Entry entry))
        {
            declaration = entry.Statement;
            return true;
        }

        if (parent is not null)
        {
            return parent.TryGet(name, out declaration);
        }

        declaration = null;
        return false;
    }
}
