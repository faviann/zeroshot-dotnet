This standalone .NET 10 consumer references the packed `Zeroshot.Client` NuGet
package, with no project reference or SDK build assertion. It only reads discovery
from an existing target. The consuming process needs neither Python nor a native
executable. `tools/native-witness/run.sh` packs the library, copies this project to
a fresh directory, restores from an isolated package cache, and runs it against
an independently launched stock native target.

For an existing direct target, pack to a local feed, restore this project using
that feed plus nuget.org for dependencies, then run with the target origin as the
only argument. Do not set `--no-restore` until the local package has been restored.
