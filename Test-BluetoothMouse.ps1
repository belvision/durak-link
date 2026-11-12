[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]
$resultType = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderResult, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1

$hidServiceId = [guid]'00001812-0000-1000-8000-00805f9b34fb'
try {
    $operation = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider]::CreateAsync($hidServiceId)
    $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
    if (-not $task.Wait(10000)) { throw 'Windows did not return a result within 10 seconds.' }
    $status = [string]$task.Result.Error
} catch {
    $failure = $_.Exception.GetBaseException()
    Write-Output ('HID probe failed: {0} (HRESULT 0x{1:X8})' -f $failure.Message, $failure.HResult)
    exit 2
}

Write-Output "Windows BLE HID service creation: $status"
if ($status -ne 'Success') { exit 1 }
# Do not advertise the service or alter the existing iPhone pairing.
