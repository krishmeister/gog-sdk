using System.Runtime.CompilerServices;

// The Editor test assembly exercises the internal injection seam
// (GogManifestInjector.InjectInto) against real manifest files.
[assembly: InternalsVisibleTo("Gog.Sdk.EditorTests")]
