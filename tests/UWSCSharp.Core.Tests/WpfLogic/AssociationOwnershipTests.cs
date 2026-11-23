using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class AssociationOwnershipTests
{
    private const string Path = @"C:\Apps\UWSCSharp.Association.exe";
    private const string Command = "\"C:\\Apps\\UWSCSharp.Association.exe\" \"%1\"";

    [Fact]
    public void EmptyRegistrationIsAbsent()
    {
        Assert.Equal(AssociationOwnership.Absent,
            AssociationOwnershipPolicy.Classify(new(), Path));
    }

    [Fact]
    public void CompleteMatchingRegistrationIsOwned()
    {
        Assert.Equal(AssociationOwnership.Owned,
            AssociationOwnershipPolicy.Classify(Owned(), Path.ToLowerInvariant()));
    }

    [Fact]
    public void ExistingRegistrationWithoutTokenIsStillOwned()
    {
        Assert.Equal(AssociationOwnership.Owned,
            AssociationOwnershipPolicy.Classify(Owned() with
            {
                OwnerToken = null
            }, Path));
    }

    [Fact]
    public void PartialModifiedAndOtherOwnersAreConflicts()
    {
        AssociationSnapshot owned = Owned();
        AssociationSnapshot[] conflicts =
        [
            owned with { CandidateExists = false },
            owned with { OpenWithValueExists = false },
            owned with { HasUnexpectedData = true },
            owned with { OwnerPath = @"C:\Other\UWSCSharp.Association.exe" },
            owned with { OwnerToken = "invalid" },
            owned with { Command = "changed" },
            owned with { ExpectedCommand = null }
        ];
        foreach (AssociationSnapshot conflict in conflicts)
        {
            Assert.Equal(AssociationOwnership.Conflict,
                AssociationOwnershipPolicy.Classify(conflict, Path));
        }
    }

    private static AssociationSnapshot Owned()
        => new(true, true, false, Path, Guid.Empty.ToString("N"), Command, Command);
}
