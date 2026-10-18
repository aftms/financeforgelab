using FinanceForgeLab.Infrastructure;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class BankDocumentSummaryTests
{
    private static BankImportSummary Part(long id, string doc, int section, int rows, string status="Draft", bool errors=false) =>
        new(id,"same-name.pdf","masked",new(2025,1,1),new(2025,1,31),rows,status,1) { DocumentId=doc,SectionIndex=section,HasErrors=errors };
    [Fact] public void OneDocumentCombinesAccountsAndSkipsEmptySectionsForReview()
    {
        var doc=Assert.Single(BankDocumentSummary.Group([Part(1,"a",0,0),Part(2,"a",1,4),Part(3,"a",2,2)]));
        Assert.Equal(6,doc.Rows);Assert.Equal(6,doc.PendingRows);Assert.Equal(2,doc.Next!.Id);
        Assert.Equal(3,doc.Parts.Count);
    }
    [Fact] public void SameFilenamesDoNotCombineDifferentPdfs()
    {
        var docs=BankDocumentSummary.Group([Part(1,"a",0,4),Part(2,"b",0,5)]);
        Assert.Equal(2,docs.Count);Assert.Equal("b",docs[0].Id);
    }
    [Fact] public void ReuploadsAfterVoidCountOnlyTheCurrentReview()
    {
        var doc=Assert.Single(BankDocumentSummary.Group([Part(1,"a",0,4,"Voided"),Part(2,"a",0,4),Part(3,"a",1,2,"Imported"),Part(4,"a",2,0)]));
        Assert.Equal(6,doc.Rows);Assert.Equal(4,doc.PendingRows);Assert.Equal(2,doc.Next!.Id);
    }
    [Fact] public void EmptyValidDocumentNeedsNoReviewButReadingErrorsStayAccessible()
    {
        Assert.Null(Assert.Single(BankDocumentSummary.Group([Part(1,"a",0,0)])).Next);
        var doc=Assert.Single(BankDocumentSummary.Group([Part(1,"a",0,4),Part(2,"a",1,0,errors:true)]));
        Assert.True(doc.HasErrors);Assert.Equal(2,doc.Next!.Id);
    }
    [Fact] public void EmptyDraftSectionsDoNotKeepAnImportedDocumentPending()
    {
        var doc=Assert.Single(BankDocumentSummary.Group([Part(1,"a",0,4,"Imported"),Part(2,"a",1,0)]));
        Assert.Equal(0,doc.PendingRows);Assert.Equal("Imported",doc.Next!.Status);
    }
    [Fact] public void PartialProgressSeparatesUnclassifiedRowsFromRecordedAndExcludedRows()
    {
        var doc=Assert.Single(BankDocumentSummary.Group([
            Part(1,"a",0,10) with {RegisteredRows=4,ExcludedRows=1,ClassifiedRows=2,AwaitingLinkRows=1},
            Part(2,"a",1,1) with {RegisteredRows=1}]));
        Assert.Equal(11,doc.Rows);Assert.Equal(5,doc.RegisteredRows);Assert.Equal(1,doc.ExcludedRows);
        Assert.Equal(5,doc.PendingRows);Assert.Equal(2,doc.ClassifiedRows);Assert.Equal(1,doc.AwaitingLinkRows);Assert.Equal(2,doc.UnclassifiedRows);
    }
    [Fact] public void NextSectionSkipsADraftWhoseRowsAreAlreadyRecordedOrExcluded()
    {
        var doc=Assert.Single(BankDocumentSummary.Group([Part(1,"a",0,3) with{RegisteredRows=2,ExcludedRows=1},Part(2,"a",1,2)]));
        Assert.Equal(2,doc.Next!.Id);Assert.Equal(2,doc.PendingRows);
        var completed=Assert.Single(BankDocumentSummary.Group([Part(1,"a",0,1) with{RegisteredRows=1}]));
        Assert.Equal(0,completed.PendingRows);Assert.Equal(0,completed.UnclassifiedRows);
    }
}
