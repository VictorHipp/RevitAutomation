#if NETFRAMEWORK
// Compatibilidad para Revit 2023 y 2024 (.NET Framework 4.8).
// El código del complemento está escrito para .NET 8/10; aquí se añaden, con el mismo nombre y comportamiento, los
// miembros de la biblioteca base que .NET Framework 4.8 no tiene. Solo se compila para net48: en .NET 8/10 se usan los
// originales. Directory.Build.props importa este espacio de nombres en los proyectos del complemento (global using).
using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CadMepRevit.Compat;

/// <summary>Comparador por referencia (en .NET Framework existe, pero es interno).</summary>
public sealed class ReferenceEqualityComparer : IEqualityComparer<object?>, IEqualityComparer
{
    private ReferenceEqualityComparer() { }
    public static ReferenceEqualityComparer Instance { get; } = new();
    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
    public int GetHashCode(object? obj) => RuntimeHelpers.GetHashCode(obj!);
}

/// <summary>Opciones de recorrido de carpetas (System.IO.EnumerationOptions de .NET).</summary>
public sealed class EnumerationOptions
{
    public bool RecurseSubdirectories { get; set; }
    public bool IgnoreInaccessible { get; set; } = true;
}

/// <summary>Añade cada argumento, entrecomillado si hace falta, a ProcessStartInfo.Arguments.</summary>
internal sealed class ArgumentListAdapter : System.Collections.ObjectModel.Collection<string>
{
    private readonly System.Diagnostics.ProcessStartInfo _psi;
    public ArgumentListAdapter(System.Diagnostics.ProcessStartInfo psi) { _psi = psi; }

    protected override void InsertItem(int index, string item)
    {
        base.InsertItem(index, item);
        string quoted = item.Length > 0 && item.IndexOfAny(new[] { ' ', '\t', '"' }) < 0
            ? item
            : "\"" + item.Replace("\"", "\\\"") + (item.EndsWith("\\") ? "\\" : "") + "\"";
        _psi.Arguments = string.IsNullOrEmpty(_psi.Arguments) ? quoted : _psi.Arguments + " " + quoted;
    }
}

/// <summary>Carácter Unicode completo (System.Text.Rune de .NET), con lo que usa el complemento.</summary>
public readonly struct Rune
{
    private readonly string _text;
    internal Rune(string text) { _text = text; }
    public int Value => char.ConvertToUtf32(_text, 0);
    public override string ToString() => _text;
    public static UnicodeCategory GetUnicodeCategory(Rune r) => CharUnicodeInfo.GetUnicodeCategory(r._text, 0);
    public static bool IsLetterOrDigit(Rune r) => char.IsLetterOrDigit(r._text, 0);
    public static bool IsLetter(Rune r) => char.IsLetter(r._text, 0);
    public static bool IsDigit(Rune r) => char.IsDigit(r._text, 0);
    public static bool IsWhiteSpace(Rune r) => char.IsWhiteSpace(r._text, 0);
}

public static class NetFrameworkCompat
{
    // ---- Miembros estáticos (miembros de extensión de C# 14) ----
    extension(double)
    {
        public static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
    }

    extension(float)
    {
        public static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }

    extension(Math)
    {
        public static double Clamp(double value, double min, double max)
        {
            if (min > max) throw new ArgumentException("min > max");
            return value < min ? min : value > max ? max : value;
        }
        public static int Clamp(int value, int min, int max)
        {
            if (min > max) throw new ArgumentException("min > max");
            return value < min ? min : value > max ? max : value;
        }
        public static long Clamp(long value, long min, long max)
        {
            if (min > max) throw new ArgumentException("min > max");
            return value < min ? min : value > max ? max : value;
        }
        public static float Clamp(float value, float min, float max)
        {
            if (min > max) throw new ArgumentException("min > max");
            return value < min ? min : value > max ? max : value;
        }
    }

    extension(StringSplitOptions)
    {
        /// <summary>Quitar espacios de cada parte: solo lo entienden las sobrecargas de Split de este archivo.</summary>
        public static StringSplitOptions TrimEntries => TrimEntriesFlag;
    }

    extension(Directory)
    {
        public static IEnumerable<string> EnumerateFiles(string path, string searchPattern, EnumerationOptions options)
        {
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                string[] files, subdirs;
                try
                {
                    files = Directory.GetFiles(dir, searchPattern);
                    subdirs = options.RecurseSubdirectories ? Directory.GetDirectories(dir) : Array.Empty<string>();
                }
                catch (Exception ex) when (options.IgnoreInaccessible && (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException))
                {
                    if (dir == path) throw;
                    continue;
                }
                foreach (var f in files) yield return f;
                for (int i = subdirs.Length - 1; i >= 0; i--) pending.Push(subdirs[i]);
            }
        }
    }

    extension(ArgumentNullException)
    {
        public static void ThrowIfNull(object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null) throw new ArgumentNullException(paramName);
        }
    }

    extension(Path)
    {
        public static string GetRelativePath(string relativeTo, string path)
        {
            string from = Path.GetFullPath(relativeTo).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string to = Path.GetFullPath(path);
            if (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) return to;
            var a = from.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var b = to.TrimEnd(Path.DirectorySeparatorChar).Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            int common = 0;
            while (common < a.Length && common < b.Length && string.Equals(a[common], b[common], StringComparison.OrdinalIgnoreCase)) common++;
            var parts = Enumerable.Repeat("..", a.Length - common).Concat(b.Skip(common)).ToArray();
            return parts.Length == 0 ? "." : string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }
    }

    extension(File)
    {
        public static void Move(string sourceFileName, string destFileName, bool overwrite)
        {
            if (overwrite && File.Exists(destFileName)) File.Delete(destFileName);
            File.Move(sourceFileName, destFileName);
        }
    }

    extension(System.Diagnostics.ProcessStartInfo psi)
    {
        /// <summary>Argumentos uno a uno: se añaden entrecomillados a <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/>.</summary>
        public ICollection<string> ArgumentList => new ArgumentListAdapter(psi);
    }

    extension(Encoding)
    {
        public static Encoding Latin1 => Encoding.GetEncoding(28591);
    }

    extension(Array)
    {
        public static void Fill<T>(T[] array, T value)
        {
            for (int i = 0; i < array.Length; i++) array[i] = value;
        }
    }

    extension(Enum)
    {
        public static TEnum[] GetValues<TEnum>() where TEnum : struct, Enum => (TEnum[])Enum.GetValues(typeof(TEnum));
        public static string[] GetNames<TEnum>() where TEnum : struct, Enum => Enum.GetNames(typeof(TEnum));
        public static bool IsDefined<TEnum>(TEnum value) where TEnum : struct, Enum => Enum.IsDefined(typeof(TEnum), value);
        public static TEnum Parse<TEnum>(string value) where TEnum : struct, Enum => (TEnum)Enum.Parse(typeof(TEnum), value);
        public static TEnum Parse<TEnum>(string value, bool ignoreCase) where TEnum : struct, Enum => (TEnum)Enum.Parse(typeof(TEnum), value, ignoreCase);
    }

    // ---- Métodos de instancia ----
    public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key) where TKey : notnull =>
        d.TryGetValue(key, out var v) ? v : default;

    public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key, TValue defaultValue) where TKey : notnull =>
        d.TryGetValue(key, out var v) ? v : defaultValue;

    public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> d, TKey key) =>
        d.TryGetValue(key, out var v) ? v : default;

    public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> d, TKey key, TValue defaultValue) =>
        d.TryGetValue(key, out var v) ? v : defaultValue;

    public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key, TValue value) where TKey : notnull
    {
        if (d.ContainsKey(key)) return false;
        d.Add(key, value);
        return true;
    }

    public static bool Remove<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key, out TValue value) where TKey : notnull
    {
        if (d.TryGetValue(key, out value!)) { d.Remove(key); return true; }
        value = default!;
        return false;
    }

    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kv, out TKey key, out TValue value)
    {
        key = kv.Key;
        value = kv.Value;
    }

    // string: sobrecargas con char que .NET Framework solo tiene con string o char[]; TrimEntries se aplica aquí.
    public static string[] Split(this string s, char separator, StringSplitOptions options) => SplitCore(s, new[] { separator }, options);
    public static string[] Split(this string s, string separator, StringSplitOptions options = StringSplitOptions.None)
    {
        bool trim = (options & TrimEntriesFlag) != 0;
        bool remove = (options & StringSplitOptions.RemoveEmptyEntries) != 0;
        var parts = s.Split(new[] { separator }, StringSplitOptions.None).Select(p => trim ? p.Trim() : p);
        return (remove ? parts.Where(p => p.Length > 0) : parts).ToArray();
    }

    private const StringSplitOptions TrimEntriesFlag = (StringSplitOptions)2;

    private static string[] SplitCore(string s, char[] separators, StringSplitOptions options)
    {
        bool trim = (options & TrimEntriesFlag) != 0;
        bool remove = (options & StringSplitOptions.RemoveEmptyEntries) != 0;
        var parts = s.Split(separators, StringSplitOptions.None).Select(p => trim ? p.Trim() : p);
        return (remove ? parts.Where(p => p.Length > 0) : parts).ToArray();
    }
    public static bool Contains(this string s, char c) => s.IndexOf(c) >= 0;
    public static bool Contains(this string s, string value, StringComparison comparison) => s.IndexOf(value, comparison) >= 0;
    public static IEnumerable<Rune> EnumerateRunes(this string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return new Rune(s.Substring(i, 2));
                i++;
            }
            else yield return new Rune(char.IsSurrogate(s[i]) ? "�" : s[i].ToString());
        }
    }

    public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;
    public static bool EndsWith(this string s, char c) => s.Length > 0 && s[s.Length - 1] == c;
    public static string Replace(this string s, string oldValue, string? newValue, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(oldValue)) throw new ArgumentException("oldValue vacío");
        var sb = new StringBuilder();
        int i = 0;
        while (true)
        {
            int j = s.IndexOf(oldValue, i, comparison);
            if (j < 0) break;
            sb.Append(s, i, j - i).Append(newValue);
            i = j + oldValue.Length;
        }
        return sb.Append(s, i, s.Length - i).ToString();
    }

    // MatchCollection: en .NET Framework no es IEnumerable<Match>.
    public static IEnumerable<Match> AsEnumerable(this MatchCollection m) => m.Cast<Match>();
    public static IEnumerable<TResult> Select<TResult>(this MatchCollection m, Func<Match, TResult> selector) => m.Cast<Match>().Select(selector);
    public static IEnumerable<Match> Where(this MatchCollection m, Func<Match, bool> predicate) => m.Cast<Match>().Where(predicate);
    public static bool Any(this MatchCollection m) => m.Count > 0;
    public static bool Any(this MatchCollection m, Func<Match, bool> predicate) => m.Cast<Match>().Any(predicate);
    public static List<Match> ToList(this MatchCollection m) => m.Cast<Match>().ToList();
    public static Match? FirstOrDefault(this MatchCollection m) => m.Count > 0 ? m[0] : null;
    public static Match? FirstOrDefault(this MatchCollection m, Func<Match, bool> predicate) => m.Cast<Match>().FirstOrDefault(predicate);
    public static Match? LastOrDefault(this MatchCollection m) => m.Count > 0 ? m[m.Count - 1] : null;

    public static bool TryDequeue<T>(this Queue<T> q, out T result)
    {
        if (q.Count > 0) { result = q.Dequeue(); return true; }
        result = default!;
        return false;
    }

    public static bool TryPop<T>(this Stack<T> s, out T result)
    {
        if (s.Count > 0) { result = s.Pop(); return true; }
        result = default!;
        return false;
    }

    public static bool TryPeek<T>(this Queue<T> q, out T result)
    {
        if (q.Count > 0) { result = q.Peek(); return true; }
        result = default!;
        return false;
    }

    // LINQ de .NET 6.
    public static TSource? MinBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector) =>
        ExtremeBy(source, keySelector, Comparer<TKey>.Default, -1);
    public static TSource? MaxBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector) =>
        ExtremeBy(source, keySelector, Comparer<TKey>.Default, 1);
    public static TSource? MinBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey>? comparer) =>
        ExtremeBy(source, keySelector, comparer ?? Comparer<TKey>.Default, -1);
    public static TSource? MaxBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey>? comparer) =>
        ExtremeBy(source, keySelector, comparer ?? Comparer<TKey>.Default, 1);

    // Igual que .NET: devuelve el primero de los empatados; secuencia vacía → default (tipos referencia) o excepción (valor).
    private static TSource? ExtremeBy<TSource, TKey>(IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey> comparer, int sign)
    {
        using var e = source.GetEnumerator();
        if (!e.MoveNext())
        {
            if (default(TSource) == null) return default;
            throw new InvalidOperationException("La secuencia no contiene elementos");
        }
        TSource best = e.Current;
        TKey bestKey = keySelector(best);
        while (e.MoveNext())
        {
            TKey key = keySelector(e.Current);
            if (sign * comparer.Compare(key, bestKey) > 0) { best = e.Current; bestKey = key; }
        }
        return best;
    }

    public static IEnumerable<TSource> DistinctBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector) =>
        DistinctBy(source, keySelector, null);
    public static IEnumerable<TSource> DistinctBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IEqualityComparer<TKey>? comparer)
    {
        var seen = new HashSet<TKey>(comparer);
        foreach (var item in source)
            if (seen.Add(keySelector(item))) yield return item;
    }

    public static IEnumerable<T[]> Chunk<T>(this IEnumerable<T> source, int size)
    {
        var buffer = new List<T>(size);
        foreach (var item in source)
        {
            buffer.Add(item);
            if (buffer.Count == size) { yield return buffer.ToArray(); buffer.Clear(); }
        }
        if (buffer.Count > 0) yield return buffer.ToArray();
    }

    public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source) => source.OrderBy(x => x);
    public static IOrderedEnumerable<T> OrderDescending<T>(this IEnumerable<T> source) => source.OrderByDescending(x => x);
}
#endif
