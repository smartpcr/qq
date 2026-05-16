/// <summary>
/// Test seam for <c>WebApplicationFactory&lt;Program&gt;</c>. The Worker
/// project uses top-level statements (<see cref="Program"/> is the
/// synthesized class containing the implicit <c>Main</c>) and the
/// generated class is <c>internal</c> by default, which prevents the
/// <c>Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory</c> from
/// reflectively bootstrapping the host in tests. Declaring this
/// <c>public partial</c> definition in the global namespace (matching
/// the top-level-statement-synthesized class's namespace) merges with
/// the synthesized partial and widens the access modifier.
/// </summary>
public partial class Program
{
}
