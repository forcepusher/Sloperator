param([int]$Port = 5713, [string]$Out)
# A stand-in server listing a Haiku, a Sonnet and an Opus. Auto's evaluation (a request with "side task for
# Sloperator") answers with a long why and a few notes, like the real helper does, so the Auto card can be
# seen with a reason that doesn't fit its line. The run's own requests answer a short reply.
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
function Send($ctx, [string]$body, [string]$type) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    $ctx.Response.ContentType = $type
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}
function Chunk($delta) { "data: " + (@{ id = "x"; object = "chat.completion.chunk"; choices = @(@{ index = 0; delta = $delta }) } | ConvertTo-Json -Compress -Depth 10) + "`n`n" }
function Say([string]$text) { (Chunk @{ role = "assistant"; content = $text }) + 'data: {"id":"x","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}' + "`n`ndata: [DONE]`n`n" }
$why = "The user is asking a planning question about how to split the settings work into steps, which needs some reasoning across several files but no deep architecture changes, so the daily driver at medium effort fits."
$found = "- Settings live in ``src/Settings/SettingsStore.cs``; the view model is ``SettingsViewModel.cs`` (about 900 lines).`n- Tests for them are in ``tests/SettingsTests.cs``, 42 of them.`n- ``docs/notes.md`` has a section on how settings are saved."
while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $path = $ctx.Request.Url.AbsolutePath
    if ($path -like "*/models") {
        Send $ctx '{"object":"list","data":[{"id":"claude-haiku-5-5","object":"model"},{"id":"claude-sonnet-5-5","object":"model"},{"id":"claude-opus-5-5","object":"model"}]}' "application/json"
        continue
    }
    if ($path -notlike "*/chat/completions") { $ctx.Response.StatusCode = 404; $ctx.Response.Close(); continue }
    $req = (New-Object IO.StreamReader($ctx.Request.InputStream)).ReadToEnd()
    $json = $req | ConvertFrom-Json
    $side = $req.Contains("side task for Sloperator")
    if ($Out) { Add-Content "$Out\log.txt" "$(Get-Date -f HH:mm:ss.fff) model=$($json.model) effort=$($json.reasoning_effort) side=$side" }
    Start-Sleep -Milliseconds 900
    if ($side) {
        Send $ctx (Say "model: daily`neffort: medium`nwhy: $why`nfound:`n$found") "text/event-stream"
    } else {
        Send $ctx (Say "I'd split it into three steps: the store first, then the view model, then the tests. Want me to start on the store?") "text/event-stream"
    }
}
