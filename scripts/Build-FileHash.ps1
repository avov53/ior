# Some Windows PowerShell installations cannot autoload Get-FileHash.
# Keep build verification available without changing the machine's module setup.
if(!(Get-Command Get-FileHash -ErrorAction SilentlyContinue)){
    function Get-FileHash {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory=$true,ValueFromPipelineByPropertyName=$true)]
            [Alias('FullName','Path')][string[]]$LiteralPath,
            [ValidateSet('SHA256')][string]$Algorithm='SHA256'
        )
        process {
            foreach($file in $LiteralPath){
                $resolved=(Resolve-Path -LiteralPath $file -ErrorAction Stop).ProviderPath
                $stream=[IO.File]::OpenRead($resolved)
                $sha=[Security.Cryptography.SHA256]::Create()
                try {$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}
                finally {$sha.Dispose();$stream.Dispose()}
                [pscustomobject]@{Algorithm='SHA256';Hash=$hash;Path=$resolved}
            }
        }
    }
}
