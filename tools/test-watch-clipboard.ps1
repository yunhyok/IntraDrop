# Windows smoke test; does not read or write clipboard contents or send UI input.
$ErrorActionPreference = 'Stop'
$log = [IO.Path]::GetTempFileName()
try {
    $output = & (Join-Path $PSScriptRoot 'watch-clipboard.ps1') -Seconds 1 -LogPath $log
    $sample = $output | Where-Object { $_ -match 'event=(clipboard\+)?ui seq=\d+ delta=\d+ ' } | Select-Object -First 1
    if (!$sample) { throw 'The watcher did not emit its initial metadata snapshot.' }
    foreach ($field in @('owner=0x', 'fg=0x', 'active=0x', 'focus=0x', 'menu=0x', 'capture=', 'held=', 'guiAvailable=')) {
        if (!$sample.Contains($field)) { throw "Missing metadata field: $field" }
    }
    if (!(Get-Content -LiteralPath $log).Contains($sample)) { throw 'The snapshot was not saved to the log.' }
    'PASS: clipboard watcher emits and saves a metadata snapshot.'
}
finally {
    Remove-Item -LiteralPath $log
}
