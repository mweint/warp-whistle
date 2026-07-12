namespace Smb3Editor.Core.Tests;

public sealed class BankAllocatorTests
{
    [Fact]
    public void AllocatesFirstSafeGapWithoutOverlapping()
    {
        var allocator = new BankAllocator(100, 100, [new BankRange(100, 10), new BankRange(130, 20)]);

        var first = allocator.Allocate(20);
        var second = allocator.Allocate(30);

        Assert.True(first.IsSuccess);
        Assert.Equal(new BankRange(110, 20), first.Value);
        Assert.True(second.IsSuccess);
        Assert.Equal(new BankRange(150, 30), second.Value);
    }

    [Fact]
    public void FullBankReturnsDiagnosticInsteadOfThrowing()
    {
        var allocator = new BankAllocator(0, 16, [new BankRange(0, 16)]);

        var allocation = allocator.Allocate(1);

        Assert.False(allocation.IsSuccess);
        Assert.Contains(allocation.Diagnostics, static diagnostic => diagnostic.Code == "ALLOC_FULL");
    }
}

