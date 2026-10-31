namespace BrainX.Core.Services;

/// <summary>
/// The last step of temp-then-move, for a file somebody else reads.
///
/// A move over a file is refused while anybody has it open, however they opened
/// it: 311 of 3,000 moves with a reader there every millisecond, and still 243
/// of 3,000 with that reader sharing everything, so nothing on the reader's side
/// helps. All of them were through within 115 ms of trying again. Plenty of
/// writers swallow a failure here because the write is only a heartbeat or a
/// status, and the disk then stays behind with nothing said.
/// </summary>
public static class AtomicFile
{
    private const int Tries = 40;

    /// <summary>
    /// Move <paramref name="tmp"/> over <paramref name="path"/>, waiting out a
    /// reader for about half a second. Still refused after that, the exception
    /// comes out as File.Move's would and the temp file is gone: most of these
    /// land in the owner's vault, and nothing sweeps up after every writer.
    /// Not for a move that decides something, like taking mail out of an inbox:
    /// there a refusal is the answer.
    /// </summary>
    public static void MoveOver(string tmp, string path)
    {
        for (var tries = 0; ; tries++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when (IsBusy(ex) && tries < Tries) { Thread.Sleep(10); }
            catch
            {
                try { File.Delete(tmp); } catch { /* the failure that matters is the one going out */ }
                throw;
            }
        }
    }

    /// <summary>Somebody is in the way, as opposed to there being nothing to
    /// move or nowhere to put it, which no amount of waiting fixes.</summary>
    private static bool IsBusy(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException && ex is not (FileNotFoundException or DirectoryNotFoundException));
}
