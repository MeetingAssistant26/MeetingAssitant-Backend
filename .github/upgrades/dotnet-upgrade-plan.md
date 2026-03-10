# .NET 10.0 Upgrade Plan

## Execution Steps

Execute steps below sequentially one by one in the order they are listed.

1. Validate that a .NET 10.0 SDK required for this upgrade is installed on the machine and if not, help to get it installed.
2. Ensure that the SDK version specified in global.json files is compatible with the .NET 10.0 upgrade.
3. Upgrade MeetingAssistant\MeetingAssistant.csproj

## Settings

This section contains settings and data used by execution steps.

### Excluded projects

No projects are excluded from this upgrade.

### Aggregate NuGet packages modifications across all projects

NuGet packages used across all selected projects or their dependencies that need version update in projects that reference them.

| Package Name                                        | Current Version | New Version | Description                    |
|:----------------------------------------------------|:---------------:|:-----------:|:-------------------------------|
| Microsoft.AspNetCore.Authentication.JwtBearer       | 9.0.13          | 10.0.3      | Recommended for .NET 10.0      |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore    | 9.0.13          | 10.0.3      | Recommended for .NET 10.0      |
| Microsoft.EntityFrameworkCore                        | 9.0.13          | 10.0.3      | Recommended for .NET 10.0      |
| Microsoft.EntityFrameworkCore.Tools                  | 9.0.13          | 10.0.3      | Recommended for .NET 10.0      |

### Project upgrade details

This section contains details about each project upgrade and modifications that need to be done in the project.

#### MeetingAssistant\MeetingAssistant.csproj modifications

Project properties changes:
  - Target framework should be changed from `net9.0` to `net10.0`

NuGet packages changes:
  - Microsoft.AspNetCore.Authentication.JwtBearer should be updated from `9.0.13` to `10.0.3` (*recommended for .NET 10.0*)
  - Microsoft.AspNetCore.Identity.EntityFrameworkCore should be updated from `9.0.13` to `10.0.3` (*recommended for .NET 10.0*)
  - Microsoft.EntityFrameworkCore should be updated from `9.0.13` to `10.0.3` (*recommended for .NET 10.0*)
  - Microsoft.EntityFrameworkCore.Tools should be updated from `9.0.13` to `10.0.3` (*recommended for .NET 10.0*)
