using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sloperator.Core.Chats;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;

namespace Sloperator.App.ViewModels;

/// <summary>A model the chat used, as the Cost estimates panel lists it: its tokens and what they cost.</summary>
public sealed record ModelCostRow(string Model, string Server, string Replies, string Input, string CacheRead, string CacheWrite, string Output, string Cost, string Source);

/// <summary>A saved chat in the panel's most expensive list.</summary>
public sealed record ChatCostRow(string Title, string When, string Cost);

/// <summary>
/// The Cost estimates panel, beside the chat: what the chat has cost so far and the tokens behind it,
/// model by model; the prices of the model in use, typed per million tokens, for servers that don't
/// say what a reply cost (a cost the server reports always wins, the user 2026-11-02); and what every
/// saved chat cost together, with the dearest ones. Nothing in it goes with a message.
/// </summary>
public sealed partial class CostEstimatesViewModel : ObservableObject, ISidePanel
{
    private readonly MainWindowViewModel? _chat;
    private readonly Func<Spending> _spent;
    private readonly Func<CostSettings> _settings;
    private readonly Func<(string BaseUrl, string Model)> _priced;
    private readonly Func<List<ChatHeader>> _chats;
    private readonly Action<ModelPrices> _save;
    private readonly Action<string> _send;

    /// <param name="chat">The chat window; null with the functions given instead (tests).</param>
    public CostEstimatesViewModel(MainWindowViewModel? chat,
        Func<Spending>? spent = null, Func<CostSettings>? settings = null, Func<(string, string)>? priced = null,
        Func<List<ChatHeader>>? chats = null, Action<ModelPrices>? save = null, Action<string>? send = null)
    {
        _chat = chat;
        _spent = spent ?? (() => chat!.Spent);
        _settings = settings ?? (() => chat!.CostSettings);
        _priced = priced ?? (() => chat!.PricedModel);
        _chats = chats ?? (() => chat!.ChatSpending());
        _save = save ?? (p => chat!.SetPrices(p));
        _send = send ?? (text => chat!.SendFromSidePanel(text));
        if (chat is not null)
        {
            chat.SpendingChanged += Refresh;
            chat.PropertyChanged += OnChatChanged;
            chat.ReplyFinished += TakeReply;
        }
        Refresh();
        RefreshAllChats();
    }

    // ---- this chat -------------------------------------------------------------------------

    /// <summary>"$0.0369", "~$0.0635", or "Not known" while a model has neither a reported cost nor prices.</summary>
    [ObservableProperty]
    private string _total = "";

    /// <summary>Where the total comes from, under it.</summary>
    [ObservableProperty]
    private string _totalNote = "";

    /// <summary>"67% of the prompt was read from the cache", or empty with no prompt yet.</summary>
    [ObservableProperty]
    private string _cacheNote = "";

    public ObservableCollection<ModelCostRow> Models { get; } = [];

    [ObservableProperty]
    private bool _hasModels;

    private void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Another model or server picked at the top: its prices in the fields.
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedModel) or nameof(MainWindowViewModel.ServerStatusText))
            ShowPrices();
        if (e.PropertyName == nameof(MainWindowViewModel.IsBusy) && _chat is { IsBusy: false })
            RunEnded();
    }

    /// <summary>Reads the chat's spending again: after each reply, when prices change, for another chat.</summary>
    public void Refresh()
    {
        var spent = _spent().Copy();
        var settings = _settings();
        Models.Clear();
        foreach (var count in spent.Models)
        {
            var cost = Spending.CostOf(count, settings);
            Models.Add(new ModelCostRow(count.Model, Host(count.BaseUrl), count.Replies.ToString(CultureInfo.InvariantCulture),
                Tokens(count.Input), Tokens(count.CacheRead), Tokens(count.CacheWrite), Tokens(count.Output),
                cost is var (amount, currency, estimate) ? Spending.Format(amount, currency, estimate) : "–",
                count.Costed > 0 ? count.Costed < count.Replies ? "Reported by the server for some replies" : "Reported by the server"
                : cost is not null ? "From your prices" : "No reported cost or prices"));
        }
        HasModels = Models.Count > 0;

        var total = spent.Total(settings);
        Total = total is var (sum, unit, guess) ? Spending.Format(sum, unit, guess) : HasModels ? "Not known" : Spending.Format(0, "$", false);
        TotalNote = !HasModels ? "Nothing sent yet in this chat."
            : total is null ? Models.Any(m => m.Cost == "–")
                ? "A model here has no reported cost and no prices: type its prices below while it is the model in use."
                : "The models' prices are in different currencies."
            : spent.Models.All(m => m.Costed > 0 && m.Costed == m.Replies) ? "As the server reported it, reply by reply."
            : spent.Models.Any(m => m.Costed > 0) ? "Partly as the server reported it, partly estimated from your prices (~)."
            : "Estimated from your prices (~).";

        long prompt = spent.Models.Sum(m => m.Input + m.CacheRead + m.CacheWrite), read = spent.Models.Sum(m => m.CacheRead);
        CacheNote = prompt == 0 ? "" : $"{(double)read / prompt:0%} of the prompt was read from the server's cache{Saved(spent, settings)}.";
        ShowPrices();
    }

    /// <summary>", saving ~$0.21" where the prices of a model say what its cache reads would have cost as plain input.</summary>
    private static string Saved(Spending spent, CostSettings settings)
    {
        var saved = spent.Models.Select(m => (m, p: settings.For(m.BaseUrl, m.Model)))
            .Where(x => x.p is { Input: { } input, CachedInput: { } cached } && input > cached && x.m.CacheRead > 0)
            .GroupBy(x => x.p!.Currency).ToList();
        if (saved.Count != 1)
            return "";
        var amount = saved[0].Sum(x => x.m.CacheRead * (x.p!.Input!.Value - x.p.CachedInput!.Value) / 1_000_000m);
        return $", saving {Spending.Format(amount, saved[0].Key, estimate: true)}";
    }

    // ---- prices of the model in use ---------------------------------------------------------

    private ModelPrices _prices = new();

    /// <summary>"qwen3-27b on 192.168.1.20:1234".</summary>
    [ObservableProperty]
    private string _pricesFor = "";

    public bool HasPricedModel => _prices.Model.Length > 0;

    private void ShowPrices()
    {
        var (url, model) = _priced();
        if (_prices.Model == model && OpenAiChatClient.NormalizeBaseUrl(_prices.BaseUrl) == OpenAiChatClient.NormalizeBaseUrl(url))
            return; // the fields are being typed in: left alone
        _prices = _settings().For(url, model) is { } typed ? Copy(typed) : new ModelPrices { BaseUrl = url, Model = model };
        PricesFor = model.Length > 0 ? $"{model} on {Host(url)}" : "No model picked at the top yet";
        OnPricesChanged();
    }

    private void OnPricesChanged()
    {
        OnPropertyChanged(nameof(HasPricedModel));
        OnPropertyChanged(nameof(CanLookUp));
        OnPropertyChanged(nameof(InputPrice));
        OnPropertyChanged(nameof(CachedInputPrice));
        OnPropertyChanged(nameof(CacheWritePrice));
        OnPropertyChanged(nameof(OutputPrice));
        OnPropertyChanged(nameof(PriceCurrency));
    }

    private static ModelPrices Copy(ModelPrices p) => new()
        { BaseUrl = p.BaseUrl, Model = p.Model, Input = p.Input, CachedInput = p.CachedInput, CacheWrite = p.CacheWrite, Output = p.Output, Currency = p.Currency };

    public string InputPrice { get => Show(_prices.Input); set => Change(() => _prices.Input = Parse(value)); }
    public string CachedInputPrice { get => Show(_prices.CachedInput); set => Change(() => _prices.CachedInput = Parse(value)); }
    public string CacheWritePrice { get => Show(_prices.CacheWrite); set => Change(() => _prices.CacheWrite = Parse(value)); }
    public string OutputPrice { get => Show(_prices.Output); set => Change(() => _prices.Output = Parse(value)); }

    public string PriceCurrency
    {
        get => _prices.Currency;
        set => Change(() => _prices.Currency = string.IsNullOrWhiteSpace(value) ? "$" : value.Trim());
    }

    private void Change(Action change)
    {
        change();
        _save(Copy(_prices));
        Refresh();
        RefreshAllChats();
    }

    private static string Show(decimal? price) => price?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>"3", "0.30" or "0,30"; anything else counts as empty.</summary>
    internal static decimal? Parse(string? text) =>
        decimal.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var price) && price >= 0 ? price : null;

    // ---- looking prices up ------------------------------------------------------------------

    /// <summary>The model whose prices a lookup asked for, until a reply gives them or the run ends.</summary>
    private ModelPrices? _lookingUp;

    /// <summary>The lookup's message went: from then on a run that ends ends the lookup.</summary>
    private bool _lookupSent;

    public bool IsLookingUp => _lookingUp is not null;

    public bool CanLookUp => HasPricedModel && !IsLookingUp;

    /// <summary>How the lookup goes: asked, filled in, or what went wrong.</summary>
    [ObservableProperty]
    private string _lookupNote = "";

    /// <summary>
    /// Look up prices: asks the agent in the chat to find what the model in use costs on its server
    /// (web_search, fetch_page) and to end its reply with a line the panel reads them from, which
    /// fills the fields (the user, 2026-11-02). The search is the agent's, in view in the chat, and
    /// the line stays in its reply, so what the fields got can be checked against what it found.
    /// </summary>
    [RelayCommand]
    private void LookUpPrices()
    {
        if (!CanLookUp)
            return;
        _lookingUp = Copy(_prices);
        _lookupSent = false;
        LookupNote = "Asked the agent to look them up on the web: the fields fill in when it answers.";
        OnLookupChanged();
        _send($"Look up the prices of {_prices.Model} on {Host(_prices.BaseUrl)}.");
        _lookupSent = true;
    }

    internal const string LinePrefix = "PRICES";

    /// <summary>What the lookup's message tells the model, with it.</summary>
    internal static string LookupContext(ModelPrices p) =>
        $"[The Cost estimates panel is open beside the chat. Look up on the web (web_search, then fetch_page) what the model {p.Model} costs " +
        $"on the server at {p.BaseUrl}, per million tokens: input, cached input (cache read), cache write and output, in the currency that server bills in. " +
        "Go by that server's own price page: a service that resells another maker's model charges its own prices. " +
        "A model the user runs on their own PC or network costs nothing: give 0 for all four. " +
        "End your reply with this one line, not in a code block, a number for each price found and null for one not found; " +
        $"the panel fills its fields from it:\n{LinePrefix} {{\"input\": 3, \"cachedInput\": 0.3, \"cacheWrite\": 3.75, \"output\": 15, \"currency\": \"$\"}}]";

    /// <summary>A reply came: one with the prices line fills the fields of the model asked about.</summary>
    public void TakeReply(string text)
    {
        if (_lookingUp is not { } asked || ReadLine(text) is not { } found)
            return;
        found.BaseUrl = asked.BaseUrl;
        found.Model = asked.Model;
        _save(found);
        if (_prices.Model == asked.Model && _prices.BaseUrl == asked.BaseUrl)
        {
            _prices = Copy(found);
            OnPricesChanged();
        }
        Refresh();
        RefreshAllChats();
        EndLookup(found.IsSet ? "Filled in from the agent's search: check them against what it found." : "The agent found no prices: type them here.");
    }

    /// <summary>The run ended: a lookup still waiting got no prices line.</summary>
    public void RunEnded()
    {
        if (_lookingUp is not null && _lookupSent)
            EndLookup("The agent's reply had no prices line: it may say why in the chat. Type them here, or look them up again.");
    }

    /// <summary>The prices in the reply's last "PRICES {…}" line; null without one that reads.</summary>
    public static ModelPrices? ReadLine(string text)
    {
        foreach (var line in text.Split('\n').Reverse())
        {
            var at = line.IndexOf(LinePrefix + " {", StringComparison.Ordinal);
            if (at < 0)
                continue;
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(line[(at + LinePrefix.Length)..].Trim().Trim('`')) is not System.Text.Json.Nodes.JsonObject json)
                    return null;
                decimal? Price(string key) => json[key] is System.Text.Json.Nodes.JsonValue v
                    ? v.TryGetValue<decimal>(out var d) ? d : v.TryGetValue<string>(out var s) ? Parse(s) : null
                    : null;
                var currency = json["currency"] is System.Text.Json.Nodes.JsonValue c && c.TryGetValue<string>(out var unit) && unit.Trim().Length > 0 ? unit.Trim() : "$";
                return new ModelPrices { Input = Price("input"), CachedInput = Price("cachedInput"), CacheWrite = Price("cacheWrite"), Output = Price("output"), Currency = currency };
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
        return null;
    }

    private void EndLookup(string note)
    {
        _lookingUp = null;
        _lookupSent = false;
        LookupNote = note;
        OnLookupChanged();
    }

    private void OnLookupChanged()
    {
        OnPropertyChanged(nameof(IsLookingUp));
        OnPropertyChanged(nameof(CanLookUp));
    }

    // ---- every saved chat ------------------------------------------------------------------

    /// <summary>"$1.24 in 12 chats", per currency.</summary>
    [ObservableProperty]
    private string _allChats = "";

    [ObservableProperty]
    private string _allChatsNote = "";

    /// <summary>The dearest chats, five at most, in the currency most chats were priced in.</summary>
    public ObservableCollection<ChatCostRow> TopChats { get; } = [];

    [ObservableProperty]
    private bool _hasTopChats;

    /// <summary>Reads every chat file's start again: when the panel opens and when prices change.</summary>
    public void RefreshAllChats()
    {
        var settings = _settings();
        var priced = new List<(ChatHeader Chat, decimal Amount, string Currency, bool Estimate)>();
        var unpriced = 0;
        foreach (var chat in _chats())
        {
            if (chat.Spent is not { IsEmpty: false } spent)
                continue;
            if (spent.Total(settings) is var (amount, currency, estimate))
                priced.Add((chat, amount, currency, estimate));
            else
                unpriced++;
        }
        var byCurrency = priced.GroupBy(p => p.Currency).OrderByDescending(g => g.Count()).ToList();
        AllChats = byCurrency.Count == 0 ? "Nothing priced yet"
            : string.Join(" · ", byCurrency.Select(g => Spending.Format(g.Sum(p => p.Amount), g.Key, g.Any(p => p.Estimate))));
        AllChatsNote = $"{Count(priced.Count, "chat")} with a cost" + (unpriced > 0 ? $"; {Count(unpriced, "chat")} without one (no reported cost or prices)." : ".")
            + " Chats saved before costs were kept aren't counted.";
        TopChats.Clear();
        if (byCurrency.Count > 0)
            foreach (var p in byCurrency[0].OrderByDescending(p => p.Amount).Take(5))
                TopChats.Add(new ChatCostRow(p.Chat.Title, p.Chat.Updated.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture), Spending.Format(p.Amount, p.Currency, p.Estimate)));
        HasTopChats = TopChats.Count > 0;
    }

    private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    internal static string Tokens(long n) => n < 1000 ? n.ToString(CultureInfo.InvariantCulture)
        : n < 1_000_000 ? (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : (n / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M";

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;

    // ---- ISidePanel: only a lookup says anything with a message ----------------------------

    public (string Via, string Context)? About(IReadOnlyList<string> files) =>
        _lookingUp is { } asked && !_lookupSent ? (SidePanelNames.CostEstimates, LookupContext(asked)) : null;

    public void Sent() { }

    public bool Drop(IReadOnlyList<string> paths) => false;

    public void Dispose()
    {
        if (_chat is null)
            return;
        _chat.SpendingChanged -= Refresh;
        _chat.PropertyChanged -= OnChatChanged;
        _chat.ReplyFinished -= TakeReply;
    }
}
