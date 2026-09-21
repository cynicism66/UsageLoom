# Actual installation is restricted to disposable GitHub-hosted runners. Never the developer PC.
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'This test may run only on a disposable GitHub-hosted runner.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(!([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'An elevated disposable runner is required.'}
$workspace=Split-Path $PSScriptRoot
$root=Join-Path $env:RUNNER_TEMP ('LoomInstallerFixture-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$upgrade=[Guid]::NewGuid().ToString('B')
$first=[Guid]::NewGuid().ToString('B')
$second=[Guid]::NewGuid().ToString('B')
$folderName='LoomFixture-'+[Guid]::NewGuid().ToString('N')
$target=Join-Path $root 'Custom install with spaces'
$wix=Join-Path $workspace '.tools/wix/wix.exe'
[xml]$source=Get-Content (Join-Path $workspace 'packaging/UsageLoom.wxs') -Raw -Encoding UTF8
$guards=($source.Wix.Package.ChildNodes | Where-Object {$_.LocalName -in @('Launch','Property') -and ($_.LocalName -eq 'Launch' -or $_.Id -eq 'LOOM_EXPECTED_USER_SID')} | ForEach-Object {$_.OuterXml}) -join "`n"
$major=($source.Wix.Package.ChildNodes | Where-Object {$_.LocalName -eq 'MajorUpgrade'}).OuterXml
function Run-FixtureMsi([string[]]$Arguments,[string]$Name,[int]$Expected=0){
    $log=Join-Path $root ($Name+'.log')
    $process=Start-Process -FilePath "$env:SystemRoot/System32/msiexec.exe" -ArgumentList ($Arguments+@('/qn','/norestart','/L*v',('"'+$log+'"'))) -Wait -PassThru
    if($process.ExitCode -ne $Expected){throw "$Name returned $($process.ExitCode), expected $Expected; log: $log"}
    if((Get-Content $log -Raw) -match '(?m)^.*(?:Error|错误) 1926\b'){throw "Rollback ACL error in $Name"}
}
try {
    foreach($version in @('1.0.0','1.0.1')){
        $product=if($version -eq '1.0.0'){$first}else{$second}
        [IO.File]::WriteAllText((Join-Path $root 'marker.txt'),$version)
        $marker=[Security.SecurityElement]::Escape((Join-Path $root 'marker.txt'))
        $xml=@"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="$folderName" Manufacturer="UsageLoom CI fixture" ProductCode="$product" UpgradeCode="$upgrade" Version="$version" Scope="$($source.Wix.Package.Scope)">
    $major
    $guards
    <MediaTemplate EmbedCab="yes" />
    <StandardDirectory Id="ProgramFilesFolder"><Directory Id="INSTALLFOLDER" Name="$folderName"><Component Id="Marker" Guid="*"><File Source="$marker" /></Component></Directory></StandardDirectory>
    <Feature Id="Main"><ComponentRef Id="Marker" /></Feature>
  </Package>
</Wix>
"@
        $fixtureSource=Join-Path $root "$version.wxs"
        [IO.File]::WriteAllText($fixtureSource,$xml)
        & $wix build $fixtureSource -o (Join-Path $root "$version.msi") -arch x64
        if($LASTEXITCODE -ne 0){throw 'Fixture MSI build failed'}
    }
    Run-FixtureMsi @('/i',('"'+(Join-Path $root '1.0.0.msi')+'"'),('INSTALLFOLDER="'+$target+'"'),('LOOM_EXPECTED_USER_SID='+$identity.User.Value)) 'install'
    $markerPath=Join-Path $target 'marker.txt'
    if([IO.File]::ReadAllText($markerPath) -ne '1.0.0'){throw 'Fixture initial installation failed'}
    # A different user or installation scope must be rejected before removal of the old files.
    Run-FixtureMsi @('/i',('"'+(Join-Path $root '1.0.1.msi')+'"'),('INSTALLFOLDER="'+$target+'"'),'LOOM_EXPECTED_USER_SID=S-1-0-0') 'wrong-user' 1603
    if([IO.File]::ReadAllText($markerPath) -ne '1.0.0'){throw 'Wrong-user preflight modified the old installation'}
    Run-FixtureMsi @('/i',('"'+(Join-Path $root '1.0.1.msi')+'"'),('INSTALLFOLDER="'+$target+'"'),'ALLUSERS=1') 'wrong-scope' 1603
    if([IO.File]::ReadAllText($markerPath) -ne '1.0.0'){throw 'Wrong-scope preflight modified the old installation'}
    Run-FixtureMsi @('/i',('"'+(Join-Path $root '1.0.1.msi')+'"'),('INSTALLFOLDER="'+$target+'"'),('LOOM_EXPECTED_USER_SID='+$identity.User.Value)) 'upgrade'
    if([IO.File]::ReadAllText($markerPath) -ne '1.0.1'){throw 'Fixture upgrade failed'}
    $installer=New-Object -ComObject WindowsInstaller.Installer
    $products=@($installer.RelatedProducts($upgrade))
    if($products.Count -ne 1 -or $products[0] -ne $second){throw 'Duplicate installation registration after upgrade'}
    Run-FixtureMsi @('/x',$second) 'uninstall'
    if(Test-Path -LiteralPath $markerPath){throw 'Fixture uninstall left the marker'}
    Write-Output 'PASS actual isolated MSI install, same-scope overwrite, user/scope rejection, single product registration, uninstall.'
} finally {
    # Only these randomly generated fixture identities; never the real UsageLoom product.
    foreach($product in @($first,$second)){
        $null=Start-Process -FilePath "$env:SystemRoot/System32/msiexec.exe" -ArgumentList @('/x',$product,'/qn','/norestart') -Wait -PassThru
    }
    Write-Output "Fixture logs retained: $root"
}
