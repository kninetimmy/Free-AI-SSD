# Diagnose-UsbDrives.ps1
# Run this with your USB SSD plugged in.
# Paste the full output back so we can see exactly what Windows reports.

$sep = "-" * 70

# ── Win32_DiskDrive ─────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Win32_DiskDrive (all disks) ===" -ForegroundColor Cyan
Write-Host $sep

$disks = Get-WmiObject -Class Win32_DiskDrive
foreach ($disk in $disks) {
    Write-Host "DeviceID      : $($disk.DeviceID)"
    Write-Host "Model         : $($disk.Model)"
    Write-Host "InterfaceType : $($disk.InterfaceType)"
    Write-Host "MediaType     : $($disk.MediaType)"
    Write-Host "PNPDeviceID   : $($disk.PNPDeviceID)"
    Write-Host "Size (GB)     : $([math]::Round($disk.Size / 1GB, 1))"

    # Walk partition → logical disk chain
    $escaped = $disk.DeviceID -replace '\\', '\\'
    $parts = Get-WmiObject -Query "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='$escaped'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"
    if ($parts) {
        foreach ($part in $parts) {
            $escapedPart = $part.DeviceID -replace '\\', '\\'
            $logicals = Get-WmiObject -Query "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='$escapedPart'} WHERE AssocClass=Win32_LogicalDiskToPartition"
            $letters = if ($logicals) { ($logicals | ForEach-Object { $_.Name }) -join ", " } else { "(no logical disk)" }
            Write-Host "  Partition    : $($part.DeviceID)  →  Drive letter(s): $letters"
        }
    } else {
        Write-Host "  Partition    : (none found)"
    }
    Write-Host $sep
}

# ── MSFT_PhysicalDisk (Storage namespace — more reliable BusType) ─────────
Write-Host ""
Write-Host "=== MSFT_PhysicalDisk (ROOT\Microsoft\Windows\Storage) ===" -ForegroundColor Cyan
Write-Host $sep

# BusType values: 1=SCSI, 2=ATAPI, 3=ATA, 4=IEEE1394, 5=SSA, 6=FC,
#                 7=USB, 8=RAID, 9=iSCSI, 10=SAS, 11=SATA, 12=SD,
#                 13=MMC, 14=Virtual, 15=FileBackedVirtual, 16=StorageSpaces,
#                 17=NVMe, 0=Unknown

$busTypeMap = @{
    0='Unknown'; 1='SCSI'; 2='ATAPI'; 3='ATA'; 4='IEEE1394'; 5='SSA';
    6='FC'; 7='USB'; 8='RAID'; 9='iSCSI'; 10='SAS'; 11='SATA'; 12='SD';
    13='MMC'; 14='Virtual'; 15='FileBackedVirtual'; 16='StorageSpaces'; 17='NVMe'
}

try {
    $physDisks = Get-WmiObject -Namespace ROOT\Microsoft\Windows\Storage -Class MSFT_PhysicalDisk -ErrorAction Stop
    foreach ($pd in $physDisks) {
        $busLabel = if ($busTypeMap.ContainsKey([int]$pd.BusType)) { $busTypeMap[[int]$pd.BusType] } else { "BusType=$($pd.BusType)" }
        Write-Host "DeviceID      : $($pd.DeviceID)"
        Write-Host "FriendlyName  : $($pd.FriendlyName)"
        Write-Host "BusType       : $($pd.BusType) ($busLabel)"
        Write-Host "MediaType     : $($pd.MediaType)"
        Write-Host "Size (GB)     : $([math]::Round($pd.Size / 1GB, 1))"
        Write-Host $sep
    }
} catch {
    Write-Host "MSFT_PhysicalDisk query failed: $_" -ForegroundColor Yellow
}

# ── DriveType from .NET (what the app currently sees) ────────────────────
Write-Host ""
Write-Host "=== .NET DriveInfo (what the app currently sees) ===" -ForegroundColor Cyan
Write-Host $sep

$drives = [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady }
foreach ($d in $drives) {
    Write-Host "$($d.Name)  DriveType=$($d.DriveType)  Format=$($d.DriveFormat)  Label='$($d.VolumeLabel)'"
}
Write-Host $sep
Write-Host ""
Write-Host "Done. Paste all output above back into the chat." -ForegroundColor Green
