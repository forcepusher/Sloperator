using System.Globalization;
using System.Xml.Linq;

namespace Sloperator.Core.Documents;

/// <summary>
/// Works out the plain formulas of a sheet Sloperator writes: numbers, cells of the same sheet, + - * / and
/// brackets, and SUM, AVERAGE, MIN, MAX, COUNT and ROUND over cells and ranges. Excel works every formula out
/// again when it opens the file; this is the value it is written with, for what shows a file without working
/// formulas out: Windows' preview, the File inspector, the chat's card, read_file. Written without one, a total
/// showed as an empty cell there (2026-11-10). Anything else (another sheet, text, a function not here, a
/// division by zero, a loop) has no value, as before.
/// </summary>
internal sealed class SheetFormula
{
    private readonly Func<string, Cell> _cell;
    private readonly string _text;
    private int _at, _depth;

    /// <summary>What a cell holds, for a formula that refers to it: a number, text (or TRUE/FALSE), or a formula with no value here.</summary>
    internal readonly record struct Cell(double? Number, bool Text = false, bool Error = false)
    {
        public static readonly Cell Empty = new(null);
    }

    private SheetFormula(string text, Func<string, Cell> cell) => (_text, _cell) = (text, cell);

    private static readonly XNamespace S = XlsxWriter.S;

    /// <summary>The most formulas waiting on one another while one is worked out.</summary>
    private const int MaxDepth = 200;

    /// <summary>
    /// Writes the value of each formula of a sheet's <paramref name="cells"/> that can be worked out here, in place of
    /// the one it had. A formula that can't keeps its own (Excel's last), and those that use it are left alone too.
    /// </summary>
    public static void WriteValues(IEnumerable<XElement> cells)
    {
        var byReference = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cells)
            if ((string?)c.Attribute("r") is { } r)
                byReference[r] = c;
        var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        var working = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double? Of(string reference, XElement f)
        {
            if (values.TryGetValue(reference, out var known))
                return known;
            // A shared formula is written out only in its first cell, an array formula fills others: Excel's to work out.
            if (f.Attribute("t") is not null || f.Value.Trim().Length == 0)
                return values[reference] = null;
            // A loop, or a chain of formulas too long to follow here (each the one under it, thousands deep, would
            // overflow the stack): no value from here, not kept, as the cell may be worked out from nearer by.
            if (working.Count >= MaxDepth || !working.Add(reference))
                return null;
            var value = Value(f.Value, Read);
            working.Remove(reference);
            return values[reference] = value;
        }
        Cell Read(string reference)
        {
            if (!byReference.TryGetValue(reference, out var c))
                return Cell.Empty;
            if (c.Element(S + "f") is { } f)
                return Of(reference, f) is { } worked ? new(worked) : new(null, Error: true);
            return (string?)c.Attribute("t") is null or "n" && double.TryParse((string?)c.Element(S + "v"), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? new(n)
                : c.Element(S + "v") is null && c.Element(S + "is") is null ? Cell.Empty : new(null, Text: true);
        }
        foreach (var (reference, c) in byReference)
        {
            if (c.Element(S + "f") is not { } f || Of(reference, f) is not { } value)
                continue;
            c.Elements(S + "v").Remove();
            if ((string?)c.Attribute("t") is "str" or "e" or "b")
                c.Attribute("t")!.Remove();
            f.AddAfterSelf(new XElement(S + "v", value.ToString("R", CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>The value of <paramref name="formula"/> (its text after =), its cells read by <paramref name="cell"/> ("C7"); null where it can't be worked out here.</summary>
    public static double? Value(string formula, Func<string, Cell> cell)
    {
        try
        {
            var f = new SheetFormula(formula, cell);
            var value = f.Sum();
            f.Space();
            return f._at == f._text.Length && double.IsFinite(value) ? value : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or ArithmeticException)
        {
            return null;
        }
    }

    private double Sum()
    {
        var value = Product();
        while (true)
        {
            if (Take('+'))
                value += Product();
            else if (Take('-'))
                value -= Product();
            else
                return value;
        }
    }

    private double Product()
    {
        var value = Unary();
        while (true)
        {
            if (Take('*'))
                value *= Unary();
            else if (Take('/'))
                value = Unary() is var by && by != 0 ? value / by : throw new FormatException("divided by zero");
            else
                return value;
        }
    }

    /// <summary>Every bracket, function and sign within another goes through here: past 200 deep no formula a person writes, and the stack would run out.</summary>
    private double Unary()
    {
        if (++_depth > 200)
            throw new FormatException("too deep");
        try
        {
            return Take('-') ? -Unary() : Take('+') ? Unary() : Atom();
        }
        finally
        {
            _depth--;
        }
    }

    private double Atom()
    {
        Space();
        if (Take('('))
        {
            var inner = Sum();
            return Take(')') ? inner : throw new FormatException("no )");
        }
        var start = _at;
        if (_at < _text.Length && (char.IsDigit(_text[_at]) || _text[_at] == '.'))
        {
            while (_at < _text.Length && (char.IsDigit(_text[_at]) || _text[_at] == '.')) _at++;
            return double.TryParse(_text[start.._at], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException("number");
        }
        var name = Word();
        if (name.Length == 0)
            throw new FormatException("nothing");
        if (Take('('))
            return Function(name.ToUpperInvariant());
        return Number(Reference(name));
    }

    private double Function(string name)
    {
        var values = new List<double>();
        var args = new List<double>();
        if (!Take(')'))
        {
            do
            {
                Space();
                var start = _at;
                var word = Word();
                if (word.Length > 0 && Take(':'))
                {
                    var (from, to) = (Reference(word), Reference(Word()));
                    // Text in a range is left out, as Excel does; a formula without a value spoils it.
                    foreach (var cell in Range(from, to).Select(_cell))
                        if (cell.Error)
                            throw new FormatException("a cell without a value");
                        else if (cell is { Number: { } n, Text: false })
                            values.Add(n);
                    args.Add(double.NaN);
                }
                else
                {
                    _at = start;
                    var value = Sum();
                    values.Add(value);
                    args.Add(value);
                }
            }
            while (Take(','));
            if (!Take(')'))
                throw new FormatException("no )");
        }
        return name switch
        {
            "SUM" => values.Sum(),
            "AVERAGE" when values.Count > 0 => values.Average(),
            "MIN" => values.Count == 0 ? 0 : values.Min(),
            "MAX" => values.Count == 0 ? 0 : values.Max(),
            "COUNT" => values.Count,
            "ROUND" when args.Count is 1 or 2 && !double.IsNaN(args[0]) && (args.Count == 1 || !double.IsNaN(args[1])) =>
                Round(args[0], args.Count == 2 ? (int)args[1] : 0),
            _ => throw new FormatException("function"),
        };
    }

    /// <summary>Excel's ROUND: halves away from zero, and a negative count of digits rounds to tens, hundreds…</summary>
    private static double Round(double value, int digits)
    {
        if (digits >= 0)
            return Math.Round(value, Math.Min(digits, 15), MidpointRounding.AwayFromZero);
        var scale = Math.Pow(10, Math.Min(-digits, 300));
        return Math.Round(value / scale, MidpointRounding.AwayFromZero) * scale;
    }

    /// <summary>A cell's number for arithmetic: an empty cell is 0, text can't be added.</summary>
    private double Number(string reference)
    {
        var cell = _cell(reference);
        return cell.Text || cell.Error ? throw new FormatException("text, or no value") : cell.Number ?? 0;
    }

    /// <summary>"B2" of "B2" or "$B$2"; anything else (another sheet's cell, a name) can't be worked out here.</summary>
    private static string Reference(string word)
    {
        var plain = word.Replace("$", "").ToUpperInvariant();
        var letters = plain.TakeWhile(char.IsAsciiLetterUpper).Count();
        return letters is > 0 and <= 3 && plain.Length > letters && plain[letters..].All(char.IsAsciiDigit) && plain[letters] != '0'
            ? plain
            : throw new FormatException("reference");
    }

    private static IEnumerable<string> Range(string from, string to)
    {
        var (r1, c1) = Split(from);
        var (r2, c2) = Split(to);
        if ((Math.Abs(r2 - r1) + 1L) * (Math.Abs(c2 - c1) + 1L) > 100_000)
            throw new FormatException("range");
        for (var r = Math.Min(r1, r2); r <= Math.Max(r1, r2); r++)
            for (var c = Math.Min(c1, c2); c <= Math.Max(c1, c2); c++)
                yield return XlsxWriter.Reference(r, c);
    }

    private static (int Row, int Column) Split(string reference)
    {
        var letters = reference.TakeWhile(char.IsAsciiLetterUpper).Count();
        var column = reference[..letters].Aggregate(0, (n, ch) => n * 26 + ch - 'A' + 1);
        return (int.Parse(reference[letters..], CultureInfo.InvariantCulture), column);
    }

    private string Word()
    {
        Space();
        var start = _at;
        while (_at < _text.Length && (char.IsAsciiLetterOrDigit(_text[_at]) || _text[_at] is '$' or '_' or '.'))
            _at++;
        return _text[start.._at];
    }

    private bool Take(char c)
    {
        Space();
        if (_at < _text.Length && _text[_at] == c)
        {
            _at++;
            return true;
        }
        return false;
    }

    private void Space()
    {
        while (_at < _text.Length && _text[_at] == ' ') _at++;
    }
}
