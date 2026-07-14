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

    [Fact]
    public void Extract_CobolTargetStatements_ReuseSingleProgramFixture()
    {
        var statements = new (string Statement, string SymbolName, string ReferenceKind)[]
        {
            ("RETURN SORT-WORK", "SORT-WORK", "reference"),
            ("RELEASE SORT-RECORD", "SORT-RECORD", "reference"),
            ("GENERATE SALES-REPORT", "SALES-REPORT", "reference"),
            ("INITIATE SALES-REPORT", "SALES-REPORT", "reference"),
            ("TERMINATE SALES-REPORT", "SALES-REPORT", "reference"),
            ("USE AFTER STANDARD ERROR PROCEDURE ON CUSTOMER-FILE", "CUSTOMER-FILE", "reference"),
            ("EXEC SQL INCLUDE CUSTOMER-CURSOR END-EXEC", "CUSTOMER-CURSOR", "reference"),
            ("EXEC SQL FETCH CUSTOMER-CURSOR INTO :CUSTOMER-ID END-EXEC", "CUSTOMER-CURSOR", "reference"),
            ("EXEC SQL OPEN CUSTOMER-CURSOR END-EXEC", "CUSTOMER-CURSOR", "reference"),
            ("EXEC SQL CLOSE CUSTOMER-CURSOR END-EXEC", "CUSTOMER-CURSOR", "reference"),
            ("EXEC SQL PREPARE CUSTOMER-STMT FROM :SQL-TEXT END-EXEC", "CUSTOMER-STMT", "reference"),
            ("EXEC SQL EXECUTE CUSTOMER-STMT END-EXEC", "CUSTOMER-STMT", "reference"),
            ("EXEC CICS LOAD PROGRAM('PRICE-PROGRAM') END-EXEC", "PRICE-PROGRAM", "reference"),
            ("EXEC CICS SEND MAP('CUSTOMER-MAP') MAPSET('CUSTOMER-SET') END-EXEC", "CUSTOMER-MAP", "reference"),
            ("EXEC CICS SEND MAP('CUSTOMER-MAP') MAPSET('CUSTOMER-SET') END-EXEC", "CUSTOMER-SET", "reference"),
            ("EXEC CICS RECEIVE MAP('CUSTOMER-MAP') INTO(CUSTOMER-AREA) END-EXEC", "CUSTOMER-MAP", "reference"),
            ("EXEC CICS READ FILE('CUSTOMER-FILE') INTO(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-FILE", "reference"),
            ("EXEC CICS WRITE FILE('CUSTOMER-FILE') FROM(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-FILE", "reference"),
            ("EXEC CICS REWRITE FILE('CUSTOMER-FILE') FROM(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-FILE", "reference"),
            ("EXEC CICS DELETE FILE('CUSTOMER-FILE') RIDFLD(CUSTOMER-KEY) END-EXEC", "CUSTOMER-FILE", "reference"),
            ("EXEC CICS STARTBR FILE('CUSTOMER-FILE') RIDFLD(CUSTOMER-KEY) END-EXEC", "CUSTOMER-FILE", "reference"),
            ("EXEC CICS READNEXT FILE('CUSTOMER-FILE') INTO(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-FILE", "reference"),
            ("EXEC CICS READPREV FILE('CUSTOMER-FILE') INTO(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-FILE", "reference"),
            ("EXEC CICS RESETBR FILE('CUSTOMER-FILE') RIDFLD(CUSTOMER-KEY) END-EXEC", "CUSTOMER-FILE", "reference"),
            ("EXEC CICS ENDBR FILE('CUSTOMER-FILE') END-EXEC", "CUSTOMER-FILE", "reference"),
            ("EXEC CICS UNLOCK FILE('CUSTOMER-FILE') END-EXEC", "CUSTOMER-FILE", "reference"),
            ("EXEC CICS READQ TS QUEUE('CUSTOMER-QUEUE') INTO(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-QUEUE", "reference"),
            ("EXEC CICS WRITEQ TS QUEUE('CUSTOMER-QUEUE') FROM(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-QUEUE", "reference"),
            ("EXEC CICS DELETEQ TS QUEUE('CUSTOMER-QUEUE') END-EXEC", "CUSTOMER-QUEUE", "reference"),
            ("EXEC CICS READQ TD QUEUE('CUSTOMER-TD') INTO(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-TD", "reference"),
            ("EXEC CICS WRITEQ TD QUEUE('CUSTOMER-TD') FROM(CUSTOMER-RECORD) END-EXEC", "CUSTOMER-TD", "reference"),
            ("EXEC CICS ENQ RESOURCE('CUSTOMER-LOCK') END-EXEC", "CUSTOMER-LOCK", "reference"),
            ("EXEC CICS DEQ RESOURCE('CUSTOMER-LOCK') END-EXEC", "CUSTOMER-LOCK", "reference"),
            ("EXEC CICS START TRANSID('PAY1') FROM(CUSTOMER-RECORD) END-EXEC", "PAY1", "reference"),
            ("EXEC CICS RETURN TRANSID('PAY1') COMMAREA(CUSTOMER-RECORD) END-EXEC", "PAY1", "reference"),
            ("EXEC CICS ASSIGN APPLID(CURRENT-APPLID) END-EXEC", "CURRENT-APPLID", "reference"),
            ("EXEC CICS ADDRESS COMMAREA(CUSTOMER-COMMAREA) END-EXEC", "CUSTOMER-COMMAREA", "reference"),
            ("EXEC CICS GETMAIN SET(CUSTOMER-PTR) FLENGTH(CUSTOMER-LENGTH) END-EXEC", "CUSTOMER-PTR", "reference"),
            ("EXEC CICS FREEMAIN DATA(CUSTOMER-PTR) END-EXEC", "CUSTOMER-PTR", "reference"),
            ("EXEC CICS RECEIVE INTO(CUSTOMER-INPUT) LENGTH(CUSTOMER-LENGTH) END-EXEC", "CUSTOMER-INPUT", "reference"),
            ("EXEC CICS SEND FROM(CUSTOMER-OUTPUT) LENGTH(CUSTOMER-LENGTH) END-EXEC", "CUSTOMER-OUTPUT", "reference"),
            ("CANCEL \"SERVICE-PROGRAM\"", "SERVICE-PROGRAM", "reference"),
            ("EXEC SQL CALL CUSTOMER-PROC(:CUSTOMER-ID) END-EXEC", "CUSTOMER-PROC", "call"),
            ("EXEC CICS LINK PROGRAM('CUSTOMER-SERVICE') END-EXEC", "CUSTOMER-SERVICE", "call"),
            ("EXEC CICS XCTL PROGRAM('NEXT-PROGRAM') END-EXEC", "NEXT-PROGRAM", "call"),
            ("EXEC CICS HANDLE CONDITION ERROR(ERROR-HANDLER) END-EXEC", "ERROR-HANDLER", "call"),
        };
        var content = $$"""
            IDENTIFICATION DIVISION.
            PROGRAM-ID. hello-world.
            PROCEDURE DIVISION.
            MAIN-SECTION SECTION.
            {{string.Join(Environment.NewLine, statements.Select(item => item.Statement).Distinct(StringComparer.Ordinal).Select(statement => $"    {statement}"))}}
                STOP RUN.
            END PROGRAM hello-world.
            """;

        var symbols = SymbolExtractor.Extract(1, "cobol", content);
        var references = ReferenceExtractor.Extract(1, "cobol", content, symbols);

        foreach (var expected in statements
                     .GroupBy(item => (item.SymbolName, item.ReferenceKind))
                     .Select(group => (group.Key.SymbolName, group.Key.ReferenceKind, Count: group.Count())))
        {
            var actualCount = references.Count(reference =>
                reference.SymbolName == expected.SymbolName
                && reference.ReferenceKind == expected.ReferenceKind
                && reference.ContainerName == "MAIN-SECTION");
            Assert.True(
                expected.Count == actualCount,
                $"Expected {expected.Count} {expected.ReferenceKind} edges for {expected.SymbolName}, found {actualCount}.");
        }
    }
}
