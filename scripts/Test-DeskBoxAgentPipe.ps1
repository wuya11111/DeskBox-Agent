param(
    [string]$PipeName = "DeskBox_Agent_7F3A9B2E",
    [string]$Method = "ping",
    [string]$ParamsJson = "{}"
)

$paramsObject = $ParamsJson | ConvertFrom-Json
$request = [ordered]@{
    id = [Guid]::NewGuid().ToString("N")
    method = $Method
    params = $paramsObject
}
$requestJson = $request | ConvertTo-Json -Compress -Depth 20

$client = [System.IO.Pipes.NamedPipeClientStream]::new(
    ".",
    $PipeName,
    [System.IO.Pipes.PipeDirection]::InOut,
    [System.IO.Pipes.PipeOptions]::Asynchronous)
$reader = $null
$writer = $null
try {
    $client.Connect(3000)
    $reader = [System.IO.StreamReader]::new(
        $client,
        [System.Text.Encoding]::UTF8,
        $true,
        4096,
        $true)
    $writer = [System.IO.StreamWriter]::new(
        $client,
        [System.Text.Encoding]::UTF8,
        4096,
        $true)
    $writer.AutoFlush = $true
    $writer.WriteLine($requestJson)
    $responseJson = $reader.ReadLine()
    if ([string]::IsNullOrWhiteSpace($responseJson)) {
        throw "DeskBox returned an empty response."
    }

    $responseJson | ConvertFrom-Json | ConvertTo-Json -Depth 20
}
finally {
    if ($writer) { $writer.Dispose() }
    if ($reader) { $reader.Dispose() }
    $client.Dispose()
}
