using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    [Fact]
    public void Extract_CobolPerform_CapturesParagraphLevelCallReference()
    {
        const string content = """
            IDENTIFICATION DIVISION.
            PROGRAM-ID. hello-world.
            PROCEDURE DIVISION.
            MAIN-SECTION SECTION.
                PERFORM HELPER-SECTION
                PERFORM HELPER-PARA THRU EXIT-PARA
                STOP RUN.
            HELPER-SECTION SECTION.
            HELPER-PARA.
                DISPLAY "A".
            MIDDLE-PARA.
                DISPLAY "B".
            EXIT-PARA.
                CALL "other-program"
            END PROGRAM hello-world.
            """;

        var symbols = SymbolExtractor.Extract(1, "cobol", content);
        var references = ReferenceExtractor.Extract(1, "cobol", content, symbols);

        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "MAIN-SECTION");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "HELPER-SECTION");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "HELPER-PARA");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "MIDDLE-PARA");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "EXIT-PARA");
        Assert.Contains(references, reference =>
            reference.SymbolName == "HELPER-SECTION"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "HELPER-PARA"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "MIDDLE-PARA"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "EXIT-PARA"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "OTHER-PROGRAM"
            && reference.ReferenceKind == "call");
        Assert.Contains(ReferenceExtractor.GetSupportedLanguages(), lang => lang == "cobol");
    }

    [Fact]
    public void Extract_CobolCopy_CapturesCopybookReference()
    {
        const string content = """
            IDENTIFICATION DIVISION.
            PROGRAM-ID. hello-world.
            PROCEDURE DIVISION.
            MAIN-SECTION SECTION.
                COPY COMMON-REC.
                STOP RUN.
            END PROGRAM hello-world.
            """;

        var symbols = SymbolExtractor.Extract(1, "cobol", content);
        var references = ReferenceExtractor.Extract(1, "cobol", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "COMMON-REC"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
    }

    [Fact]
    public void Extract_CobolCommonStatements_CapturesSearchableReferences()
    {
        const string content = """
            IDENTIFICATION DIVISION.
            PROGRAM-ID. hello-world.
            PROCEDURE DIVISION.
            MAIN-SECTION SECTION.
                GO TO NEXT-PARA
                OPEN INPUT CUSTOMER-FILE
                READ CUSTOMER-FILE
                WRITE CUSTOMER-RECORD
                SEARCH ALL CUSTOMER-TABLE
                START ORDER-FILE KEY IS >= ORDER-KEY
                SET HAS-ITEM TO TRUE
                MOVE SOURCE-VALUE TO DEST-VALUE
                ADD AMOUNT TO TOTAL
                SUBTRACT TAX FROM NET
                MULTIPLY RATE BY RESULT
                DIVIDE GRAND-TOTAL INTO AVERAGE
                COMPUTE FINAL-TOTAL = TOTAL + TAX
                STRING FIRST-NAME DELIMITED BY SIZE INTO BUFFER
                UNSTRING BUFFER INTO PART1
                DISPLAY CUSTOMER-NAME
                ACCEPT INPUT-NAME
                INSPECT BUFFER
                CLOSE CUSTOMER-FILE
                STOP RUN.
            NEXT-PARA.
                CONTINUE.
            END PROGRAM hello-world.
            """;

        var symbols = SymbolExtractor.Extract(1, "cobol", content);
        var references = ReferenceExtractor.Extract(1, "cobol", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "NEXT-PARA"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "CUSTOMER-FILE"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "CUSTOMER-TABLE"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "ORDER-FILE"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "HAS-ITEM"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "DEST-VALUE"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "FINAL-TOTAL"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "BUFFER"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "INPUT-NAME"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
        Assert.Contains(references, reference =>
            reference.SymbolName == "CUSTOMER-NAME"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
    }

    [Theory]
    [InlineData("RETURN SORT-WORK", "SORT-WORK")]
    [InlineData("RELEASE SORT-RECORD", "SORT-RECORD")]
    [InlineData("GENERATE SALES-REPORT", "SALES-REPORT")]
    [InlineData("INITIATE SALES-REPORT", "SALES-REPORT")]
    [InlineData("TERMINATE SALES-REPORT", "SALES-REPORT")]
    [InlineData("USE AFTER STANDARD ERROR PROCEDURE ON CUSTOMER-FILE", "CUSTOMER-FILE")]
    [InlineData("EXEC SQL INCLUDE CUSTOMER-CURSOR END-EXEC", "CUSTOMER-CURSOR")]
    [InlineData("EXEC SQL FETCH CUSTOMER-CURSOR INTO :CUSTOMER-ID END-EXEC", "CUSTOMER-CURSOR")]
    [InlineData("EXEC SQL OPEN CUSTOMER-CURSOR END-EXEC", "CUSTOMER-CURSOR")]
    [InlineData("EXEC SQL CLOSE CUSTOMER-CURSOR END-EXEC", "CUSTOMER-CURSOR")]
    [InlineData("EXEC SQL PREPARE CUSTOMER-STMT FROM :SQL-TEXT END-EXEC", "CUSTOMER-STMT")]
    [InlineData("EXEC SQL EXECUTE CUSTOMER-STMT END-EXEC", "CUSTOMER-STMT")]
    [InlineData("EXEC CICS LOAD PROGRAM('PRICE-PROGRAM') END-EXEC", "PRICE-PROGRAM")]
    [InlineData("EXEC CICS SEND MAP('CUSTOMER-MAP') MAPSET('CUSTOMER-SET') END-EXEC", "CUSTOMER-MAP")]
    [InlineData("EXEC CICS SEND MAP('CUSTOMER-MAP') MAPSET('CUSTOMER-SET') END-EXEC", "CUSTOMER-SET")]
    [InlineData("EXEC CICS RECEIVE MAP('CUSTOMER-MAP') INTO(CUSTOMER-AREA) END-EXEC", "CUSTOMER-MAP")]
    [InlineData("EXEC CICS READ FILE('CUSTOMER-FILE') INTO(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-FILE")]
    [InlineData("EXEC CICS WRITE FILE('CUSTOMER-FILE') FROM(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-FILE")]
    [InlineData("EXEC CICS REWRITE FILE('CUSTOMER-FILE') FROM(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-FILE")]
    [InlineData("EXEC CICS DELETE FILE('CUSTOMER-FILE') RIDFLD(CUSTOMER-KEY) END-EXEC", "CUSTOMER-FILE")]
    [InlineData("EXEC CICS STARTBR FILE('CUSTOMER-FILE') RIDFLD(CUSTOMER-KEY) END-EXEC", "CUSTOMER-FILE")]
    [InlineData("EXEC CICS READNEXT FILE('CUSTOMER-FILE') INTO(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-FILE")]
    [InlineData("EXEC CICS READPREV FILE('CUSTOMER-FILE') INTO(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-FILE")]
    [InlineData("EXEC CICS RESETBR FILE('CUSTOMER-FILE') RIDFLD(CUSTOMER-KEY) END-EXEC", "CUSTOMER-FILE")]
    [InlineData("EXEC CICS ENDBR FILE('CUSTOMER-FILE') END-EXEC", "CUSTOMER-FILE")]
    [InlineData("EXEC CICS UNLOCK FILE('CUSTOMER-FILE') END-EXEC", "CUSTOMER-FILE")]
    [InlineData("EXEC CICS READQ TS QUEUE('CUSTOMER-QUEUE') INTO(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-QUEUE")]
    [InlineData("EXEC CICS WRITEQ TS QUEUE('CUSTOMER-QUEUE') FROM(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-QUEUE")]
    [InlineData("EXEC CICS DELETEQ TS QUEUE('CUSTOMER-QUEUE') END-EXEC", "CUSTOMER-QUEUE")]
    [InlineData("EXEC CICS READQ TD QUEUE('CUSTOMER-TD') INTO(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-TD")]
    [InlineData("EXEC CICS WRITEQ TD QUEUE('CUSTOMER-TD') FROM(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-TD")]
    [InlineData("EXEC CICS ENQ RESOURCE('CUSTOMER-LOCK') END-EXEC", "CUSTOMER-LOCK")]
    [InlineData("EXEC CICS DEQ RESOURCE('CUSTOMER-LOCK') END-EXEC", "CUSTOMER-LOCK")]
    [InlineData("EXEC CICS START TRANSID('PAY1') FROM(CUSTOMER-RECORD) END-EXEC", "PAY1")]
    [InlineData("EXEC CICS RETURN TRANSID('PAY1') COMMAREA(CUSTOMER-RECORD) END-EXEC", "PAY1")]
    [InlineData("EXEC CICS ASSIGN APPLID(CURRENT-APPLID) END-EXEC", "CURRENT-APPLID")]
    [InlineData("EXEC CICS ADDRESS COMMAREA(CUSTOMER-COMMAREA) END-EXEC", "CUSTOMER-COMMAREA")]
    [InlineData("EXEC CICS GETMAIN SET(CUSTOMER-PTR) FLENGTH(CUSTOMER-LENGTH) END-EXEC", "CUSTOMER-PTR")]
    [InlineData("EXEC CICS FREEMAIN DATA(CUSTOMER-PTR) END-EXEC", "CUSTOMER-PTR")]
    [InlineData("EXEC CICS RECEIVE INTO(CUSTOMER-INPUT) LENGTH(CUSTOMER-LENGTH) END-EXEC", "CUSTOMER-INPUT")]
    [InlineData("EXEC CICS SEND FROM(CUSTOMER-OUTPUT) LENGTH(CUSTOMER-LENGTH) END-EXEC", "CUSTOMER-OUTPUT")]
    public void Extract_CobolSingleTargetStatement_CapturesSearchableReference(string statement, string expectedSymbolName)
    {
        var content = $$"""
            IDENTIFICATION DIVISION.
            PROGRAM-ID. hello-world.
            PROCEDURE DIVISION.
            MAIN-SECTION SECTION.
                {{statement}}
                STOP RUN.
            END PROGRAM hello-world.
            """;

        var symbols = SymbolExtractor.Extract(1, "cobol", content);
        var references = ReferenceExtractor.Extract(1, "cobol", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == expectedSymbolName
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
    }

    [Theory]
    [InlineData("CANCEL \"SERVICE-PROGRAM\"", "SERVICE-PROGRAM")]
    public void Extract_CobolLiteralTargetStatement_CapturesSearchableReference(string statement, string expectedSymbolName)
    {
        var content = $$"""
            IDENTIFICATION DIVISION.
            PROGRAM-ID. hello-world.
            PROCEDURE DIVISION.
            MAIN-SECTION SECTION.
                {{statement}}
                STOP RUN.
            END PROGRAM hello-world.
            """;

        var symbols = SymbolExtractor.Extract(1, "cobol", content);
        var references = ReferenceExtractor.Extract(1, "cobol", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == expectedSymbolName
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "MAIN-SECTION");
    }

    [Theory]
    [InlineData("EXEC SQL CALL CUSTOMER-PROC(:CUSTOMER-ID) END-EXEC", "CUSTOMER-PROC")]
    [InlineData("EXEC CICS LINK PROGRAM('CUSTOMER-SERVICE') END-EXEC", "CUSTOMER-SERVICE")]
    [InlineData("EXEC CICS XCTL PROGRAM('NEXT-PROGRAM') END-EXEC", "NEXT-PROGRAM")]
    [InlineData("EXEC CICS HANDLE CONDITION ERROR(ERROR-HANDLER) END-EXEC", "ERROR-HANDLER")]
    public void Extract_CobolExternalCallStatement_CapturesSearchableCall(string statement, string expectedSymbolName)
    {
        var content = $$"""
            IDENTIFICATION DIVISION.
            PROGRAM-ID. hello-world.
            PROCEDURE DIVISION.
            MAIN-SECTION SECTION.
                {{statement}}
                STOP RUN.
            END PROGRAM hello-world.
            """;

        var symbols = SymbolExtractor.Extract(1, "cobol", content);
        var references = ReferenceExtractor.Extract(1, "cobol", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == expectedSymbolName
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "MAIN-SECTION");
    }
}
