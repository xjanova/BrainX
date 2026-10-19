using System.Security.Cryptography;
using System.Text;
using BrainX.Core.Services.Cloud;

namespace BrainX.Mcp;

/// <summary>
/// Attachments in BrainX Cloud (owner, 2026-10-19: "back up attachments too —
/// images, files in Attachments and notes' embedded files, within the 4 GB
/// quota"): bytes go up and come back exact, the limit and the type list hold,
/// an older node is never sent what it refuses, conflicts keep the extension,
/// and the notes-only cache never pulls them.
/// </summary>
internal static partial class CloudSelfTest
{
    private static async Task AttachmentsAsync(string root)
    {
        using var fake = new CloudFakeServer();
        fake.ValidKeys.Add(Key);
        using var api = new CloudApiClient(null, fake.BaseUrl, timeout: TimeSpan.FromSeconds(20));
        var login = await api.LoginAsync(Key, "pc-a");
        api.SetToken(login.Token);
        var acct = login.Account.Id;
        var engine = new CloudSyncEngine(api);

        string F(string vault, string rel) => Path.Combine(vault, rel.Replace('/', Path.DirectorySeparatorChar));
        void WriteBytes(string vault, string rel, byte[] bytes)
        {
            var f = F(vault, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, bytes);
        }
        static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        // Bytes that are NOT valid UTF-8, so a text path anywhere would corrupt them.
        var png = new byte[300_000];
        new Random(7).NextBytes(png);
        png[0] = 0x89; png[1] = (byte)'P'; png[2] = (byte)'N'; png[3] = (byte)'G'; png[4] = 0xFF; png[5] = 0xFE;
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n%âã\n1 0 obj\n");
        var tooBig = new byte[CloudPathRules.MaxAttachmentBytes + 1];

        var vault = Path.Combine(root, "attach-vault");
        WriteBytes(vault, "Notes/a.md", Encoding.UTF8.GetBytes("# A\n![[pic.png]]\n"));
        WriteBytes(vault, "Attachments/pic.png", png);
        WriteBytes(vault, "Attachments/ภาพไทย.PDF", pdf);
        WriteBytes(vault, "Attachments/huge.zip", tooBig);
        WriteBytes(vault, "Attachments/tool.exe", new byte[] { 0x4D, 0x5A });
        WriteBytes(vault, "Attachments/notes.txt", Encoding.UTF8.GetBytes("not a supported type"));

        var state = CloudSyncState.Load(vault);
        state.Folders = new List<string> { "Notes", "Attachments" };
        state.Save(vault);
        CloudPushOptions Push(bool attachments = true) => new() { AccountId = acct, DeviceName = "pc-a", IncludeAttachments = attachments };
        CloudPullOptions Pull(bool attachments = true) => new() { AccountId = acct, DeviceName = "pc-a", IncludeAttachments = attachments };

        // ── 1. an older node: attachments are neither sent nor refused ──
        fake.SupportsAttachments = false;
        var old0 = fake.UploadRequests;
        var r0 = await engine.PushAsync(vault, state.Folders, state, Push());
        Check("old node: the notes go up, no attachment is sent", r0.Ok && r0.Uploaded == 1 && fake.NotesOf(acct).Keys.SequenceEqual(new[] { "Notes/a.md" }),
              $"{r0.ErrorCode} uploaded={r0.Uploaded} cloud={string.Join(",", fake.NotesOf(acct).Keys)}");
        Check("…in one request, and nothing reported as refused",
              fake.UploadRequests - old0 == 1 && r0.Skipped.Count == 0, $"requests={fake.UploadRequests - old0} skipped={string.Join(";", r0.Skipped)}");

        // ── 2. the node updates: the next sync sends them ──
        fake.SupportsAttachments = true;
        var r1 = await engine.PushAsync(vault, state.Folders, state, Push());
        var cloud = fake.NotesOf(acct);
        Check("push: the picture and the PDF go up", r1.Ok && r1.Uploaded == 2
              && cloud.ContainsKey("Attachments/pic.png") && cloud.ContainsKey("Attachments/ภาพไทย.PDF"),
              $"{r1.ErrorCode} uploaded={r1.Uploaded} cloud={string.Join(",", cloud.Keys)}");
        Check("…byte for byte (base64 on the wire, the cloud's sha over the raw bytes)",
              Convert.FromBase64String(cloud["Attachments/pic.png"]).AsSpan().SequenceEqual(png)
              && CloudSyncEngine.PayloadSha("Attachments/pic.png", cloud["Attachments/pic.png"]) == Sha(png));
        Check("…a file over 20 MB is reported, not sent",
              !cloud.ContainsKey("Attachments/huge.zip") && r1.Skipped.Any(s => s.Path == "Attachments/huge.zip" && s.Reason.Contains("20 MB")),
              string.Join(";", r1.Skipped));
        Check("…programs and unsupported types never travel",
              !cloud.Keys.Any(k => k.EndsWith(".exe") || k.EndsWith(".txt")));
        var r2 = await engine.PushAsync(vault, state.Folders, state, Push());
        Check("…and a second push sends nothing", r2.Ok && r2.Uploaded == 0, $"uploaded={r2.Uploaded}");

        // ── 3. a notes-only push does not take attachments for deleted ──
        var keep = fake.NotesOf(acct).Count;
        var r3 = await engine.PushAsync(vault, state.Folders, state, Push(attachments: false));
        Check("notes-only push: the attachments it did not look at stay in the cloud",
              r3.Ok && r3.Deleted == 0 && fake.NotesOf(acct).Count == keep, $"deleted={r3.Deleted}");

        // ── 4. a fresh install restores them, exact ──
        var fresh = Path.Combine(root, "attach-restore");
        Directory.CreateDirectory(fresh);
        var fstate = CloudSyncState.Load(fresh);
        var p1 = await engine.PullAsync(fresh, fstate.Folders, fstate, Pull());
        Check("restore: the picture comes back byte for byte",
              p1.Ok && p1.Restored && File.Exists(F(fresh, "Attachments/pic.png"))
              && File.ReadAllBytes(F(fresh, "Attachments/pic.png")).AsSpan().SequenceEqual(png),
              $"{p1.ErrorCode} fetched={p1.Fetched}");
        Check("…the Thai-named PDF too", File.Exists(F(fresh, "Attachments/ภาพไทย.PDF"))
              && File.ReadAllBytes(F(fresh, "Attachments/ภาพไทย.PDF")).AsSpan().SequenceEqual(pdf));
        var push0 = fake.UploadRequests;
        var p2 = await engine.PushAsync(fresh, fstate.Folders, fstate, Push());
        Check("…and the push after the restore sends nothing back", p2.Ok && p2.Uploaded == 0 && fake.UploadRequests == push0,
              $"uploaded={p2.Uploaded}");

        // ── 5. the notes-only cache never pulls attachments ──
        var cache = Path.Combine(root, "attach-cache");
        var c1 = await engine.PullAsync(cache, new CloudPullOptions { AccountId = acct });
        Check("cache (notes only): no attachment is written",
              c1.Ok && File.Exists(F(cache, "Notes/a.md")) && !Directory.Exists(Path.Combine(cache, "Attachments")),
              $"{c1.ErrorCode} fetched={c1.Fetched}");

        // ── 6. edited on both sides: both kept, the copy still a .png ──
        var other = (byte[])png.Clone(); other[10] ^= 0xFF;
        fake.PutNote(acct, "Attachments/pic.png", Convert.ToBase64String(other), DateTime.UtcNow.AddMinutes(5));
        var mine = (byte[])png.Clone(); mine[20] ^= 0xFF;
        File.WriteAllBytes(F(vault, "Attachments/pic.png"), mine);
        var k1 = await engine.PullAsync(vault, state.Folders, state, Pull());
        var copy = Directory.GetFiles(F(vault, "Attachments"), "pic (conflict *").FirstOrDefault();
        Check("conflict: both copies are kept and the copy keeps .png",
              k1.Ok && k1.Conflicts.Count == 1 && copy != null && copy.EndsWith(".png", StringComparison.OrdinalIgnoreCase),
              $"{k1.ErrorCode} conflicts={k1.Conflicts.Count} copy={copy}");
        var kept = new[] { File.ReadAllBytes(F(vault, "Attachments/pic.png")), copy != null ? File.ReadAllBytes(copy) : Array.Empty<byte>() };
        Check("…one is the cloud's bytes, the other ours",
              kept.Any(b => b.AsSpan().SequenceEqual(other)) && kept.Any(b => b.AsSpan().SequenceEqual(mine)));

        // ── 7. the path rules ──
        Check("path rules: pictures, PDFs, office files and archives are attachments",
              new[] { "a/b.png", "x.JPG", "a/b.pdf", "a/b.docx", "a/b.zip", "a/b.canvas", "a/ภาพ.webp" }.All(p => CloudPathRules.IsAttachment(p) && CloudPathRules.IsValid(p)));
        Check("path rules: programs, scripts, .txt and nameless files are not",
              new[] { "a/b.exe", "a/b.dll", "a/b.ps1", "a/b.txt", "a/.png", "a/b.aab" }.All(p => !CloudPathRules.IsValid(p)));
    }
}
