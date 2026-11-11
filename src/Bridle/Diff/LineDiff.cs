namespace Bridle.Diff;

internal enum DiffKind
{
    Same,
    Changed,   // iki tarafta da var ama farklı
    LeftOnly,  // yalnızca A'da
    RightOnly, // yalnızca B'de
}

/// <summary>Yan yana gösterilecek tek satır.</summary>
internal sealed record DiffRow(DiffKind Kind, int? LeftNo, string Left, int? RightNo, string Right);

/// <summary>Satır tabanlı karşılaştırma (en uzun ortak alt dizi).</summary>
internal static class LineDiff
{
    // Tablo n x m hücre tutar; bunun üstünde hizalamasız karşılaştırmaya düşülür.
    private const long MaxCells = 16_000_000;

    public static string[] SplitLines(string text)
    {
        if (text.Length == 0) return Array.Empty<string>();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        // Sondaki satır sonu boş bir satır üretmesin.
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    public static List<DiffRow> Compute(string leftText, string rightText)
    {
        string[] a = SplitLines(leftText);
        string[] b = SplitLines(rightText);

        // Ortak baş ve son kırpılır: çıktılar çoğu zaman büyük ölçüde aynıdır.
        int head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        int tail = 0;
        while (tail < a.Length - head && tail < b.Length - head &&
               a[a.Length - 1 - tail] == b[b.Length - 1 - tail])
        {
            tail++;
        }

        var rows = new List<DiffRow>(Math.Max(a.Length, b.Length));
        for (int i = 0; i < head; i++) rows.Add(new DiffRow(DiffKind.Same, i + 1, a[i], i + 1, b[i]));

        int n = a.Length - head - tail;
        int m = b.Length - head - tail;
        if ((long)n * m > MaxCells)
        {
            Unaligned(rows, a, b, head, n, m);
        }
        else
        {
            Aligned(rows, a, b, head, n, m);
        }

        for (int i = 0; i < tail; i++)
        {
            int ia = a.Length - tail + i;
            int ib = b.Length - tail + i;
            rows.Add(new DiffRow(DiffKind.Same, ia + 1, a[ia], ib + 1, b[ib]));
        }
        return rows;
    }

    private static void Aligned(List<DiffRow> rows, string[] a, string[] b, int head, int n, int m)
    {
        // lcs[i, j]: a[head+i..] ile b[head+j..] arasındaki en uzun ortak alt dizi.
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[head + i] == b[head + j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var removed = new List<int>();
        var added = new List<int>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[head + x] == b[head + y])
            {
                Flush(rows, a, b, removed, added);
                rows.Add(new DiffRow(DiffKind.Same, head + x + 1, a[head + x], head + y + 1, b[head + y]));
                x++;
                y++;
            }
            else if (y < m && (x == n || lcs[x, y + 1] > lcs[x + 1, y]))
            {
                added.Add(head + y++);
            }
            else
            {
                removed.Add(head + x++);
            }
        }
        Flush(rows, a, b, removed, added);
    }

    /// <summary>Art arda silinen ve eklenen satırları "değişti" olarak eşler.</summary>
    private static void Flush(List<DiffRow> rows, string[] a, string[] b, List<int> removed, List<int> added)
    {
        int paired = Math.Min(removed.Count, added.Count);
        for (int i = 0; i < paired; i++)
        {
            rows.Add(new DiffRow(DiffKind.Changed, removed[i] + 1, a[removed[i]], added[i] + 1, b[added[i]]));
        }
        for (int i = paired; i < removed.Count; i++)
        {
            rows.Add(new DiffRow(DiffKind.LeftOnly, removed[i] + 1, a[removed[i]], null, ""));
        }
        for (int i = paired; i < added.Count; i++)
        {
            rows.Add(new DiffRow(DiffKind.RightOnly, null, "", added[i] + 1, b[added[i]]));
        }
        removed.Clear();
        added.Clear();
    }

    private static void Unaligned(List<DiffRow> rows, string[] a, string[] b, int head, int n, int m)
    {
        for (int i = 0; i < Math.Max(n, m); i++)
        {
            bool hasA = i < n, hasB = i < m;
            string left = hasA ? a[head + i] : "";
            string right = hasB ? b[head + i] : "";
            DiffKind kind = !hasA ? DiffKind.RightOnly
                : !hasB ? DiffKind.LeftOnly
                : left == right ? DiffKind.Same : DiffKind.Changed;
            rows.Add(new DiffRow(kind, hasA ? head + i + 1 : null, left, hasB ? head + i + 1 : null, right));
        }
    }

    public static int CountDifferences(List<DiffRow> rows) => rows.Count(r => r.Kind != DiffKind.Same);
}
