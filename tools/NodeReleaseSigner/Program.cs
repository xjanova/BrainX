// Signs a published BrainX node folder so the node's self-updater will install it.
//
//   dotnet run --project tools/NodeReleaseSigner -c Release -- sign <node folder> <version>
//       key: env BRAINX_NODE_SIGNING_KEY (PKCS#8 PEM; in CI, the repository secret).
//       Writes update-manifest.json (every file's path, size, SHA-256) and
//       update-manifest.sig into the folder, then checks the result with the
//       node's own verifier against the public key compiled into the node. A
//       missing secret, or one that is not the private half of that key, fails
//       the release instead of publishing an update no node would install.
//
//   dotnet run --project tools/NodeReleaseSigner -- verify <node folder> <version>
//       Runs the node's check on a signed folder (manual spot check).
//
//   dotnet run --project tools/NodeReleaseSigner -- keygen <out.pem>
//       New P-256 key: PKCS#8 PEM to <out.pem> (never inside a git working
//       tree), the SubjectPublicKeyInfo (base64) to stdout — paste it into
//       UpdatePackageVerifier.ReleasePublicKey. Rotating the key means every
//       node must first update to a build carrying the new public key.

using System.Security.Cryptography;
using BrainX.NodeReleaseSigner;
using BrainX.Server.Services;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: NodeReleaseSigner sign <folder> <version> | verify <folder> <version> | keygen <out.pem>");
    return 2;
}

switch (args[0].ToLowerInvariant())
{
    case "sign" when args.Length == 3:
    {
        var folder = Path.GetFullPath(args[1]);
        var version = args[2].Trim().TrimStart('v', 'V');
        if (!Directory.Exists(folder)) { Console.Error.WriteLine($"folder not found: {folder}"); return 2; }

        var secret = Environment.GetEnvironmentVariable("BRAINX_NODE_SIGNING_KEY");
        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Error.WriteLine("BRAINX_NODE_SIGNING_KEY is not set. Refusing to publish a node package that no node could verify.");
            return 1;
        }

        ECDsa key;
        try { key = PackageSigner.LoadPrivateKey(secret); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 1; }

        using (key)
        {
            var problem = PackageSigner.SignFolder(folder, version, key, Convert.FromBase64String(UpdatePackageVerifier.ReleasePublicKey));
            if (problem != null) { Console.Error.WriteLine(problem); return 1; }
        }
        var count = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count() - 2;
        Console.WriteLine($"signed {count} files of {UpdatePackageVerifier.ProductSlug} {version} in {folder}");
        return 0;
    }

    case "verify" when args.Length == 3:
    {
        var verdict = UpdatePackageVerifier.Verify(args[1], args[2], "0.0.0", UpdatePackageVerifier.VersionRule.MustBeNewer);
        Console.WriteLine($"{verdict.Result}: {verdict.Detail}");
        return verdict.IsValid ? 0 : 1;
    }

    case "keygen" when args.Length == 2:
    {
        var outPath = Path.GetFullPath(args[1]);
        if (File.Exists(outPath)) { Console.Error.WriteLine($"{outPath} already exists — not overwriting a key"); return 1; }
        for (var dir = Path.GetDirectoryName(outPath); dir != null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            {
                Console.Error.WriteLine($"{outPath} is inside a git working tree ({dir}) — a private key must never be committed");
                return 1;
            }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs))
            w.Write(key.ExportPkcs8PrivateKeyPem());
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    default:
        Console.Error.WriteLine("usage: NodeReleaseSigner sign <folder> <version> | verify <folder> <version> | keygen <out.pem>");
        return 2;
}
