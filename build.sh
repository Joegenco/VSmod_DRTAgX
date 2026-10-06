#!/usr/bin/env sh
build_project_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd) || exit 1
# Pass an absolute root; Cake tasks must not depend on the caller's directory.
dotnet run --project "$build_project_root/CakeBuild/CakeBuild.csproj" -- --project-root "$build_project_root" "$@"
