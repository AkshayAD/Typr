using Ava.Infrastructure;
using Xunit;
namespace Ava.Infrastructure.Tests;
public class CancellationTests
{
 [Fact] public void CancelInvalidatesQueuedStartBeforeItRuns() { var gate=new RecordingGeneration();int start=gate.Begin();gate.Cancel();Assert.False(gate.IsCurrent(start)); }
 [Fact] public void ANewRecordingCannotReviveAnOldGeneration() { var gate=new RecordingGeneration();int old=gate.Begin();gate.Cancel();int next=gate.Begin();Assert.False(gate.IsCurrent(old));Assert.True(gate.IsCurrent(next)); }
 [Fact] public async Task CancellationIsVisibleAcrossThreadsWithoutQueueCapacity() { var gate=new RecordingGeneration();int active=gate.Begin();await Task.Run(gate.Cancel);Assert.False(gate.IsCurrent(active)); }
}
