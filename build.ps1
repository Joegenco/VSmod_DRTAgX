$buildProjectRoot = $PSScriptRoot
$buildProject = Join-Path $buildProjectRoot 'CakeBuild/CakeBuild.csproj'
# Resolve from the script, so invoking this wrapper from another directory is safe.
dotnet run --project $buildProject -- --project-root $buildProjectRoot @args
exit $LASTEXITCODE;
