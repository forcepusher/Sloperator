using System.Globalization;

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
    private int _at;

    /// <summary>What a cell holds, for a formula that refers to it: a number, text (or TRUE/FALSE), or a formula with no value here.</summary>
    internal readonly record struct Cell(double? Number, bool Text = false, bool Error = false)
    {
        public static readonly Cell Empty = new(null);
    }

    private SheetFormula(string text, Func<string, Cell> cell) => (_text, _cell) = (text, cell);

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
        catch (FormatException)
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

    private double Unary() => Take('-') ? -Unary() : Take('+') ? Unary() : Atom();

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
                Math.Round(args[0], (int)(args.Count == 2 ? args[1] : 0), MidpointRounding.AwayFromZero),
            _ => throw new FormatException("function"),
        };
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
