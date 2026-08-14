// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using ProfileExplorer.Core;
using ProfileExplorer.UI;

namespace ProfileExplorerUITests;

[TestClass]
public class FunctionListExportingTests {
  [TestMethod]
  public void PerformanceCounterValues_DistinguishMissingFromZero() {
    var counters = new PerformanceCounterSetEx(1);
    counters.Add(new PerformanceCounterSetEx.PerformanceCounterValueEx {
      CounterId = 10,
      Label = "0",
      Value = 0
    });

    Assert.AreEqual(0, counters.FindCounterValueOrNull(10));
    Assert.IsNull(counters.FindCounterValueOrNull(11));
  }

  [TestMethod]
  public void Exporters_PreservePerformanceColumnOrderAndFormattedValues() {
    var function = CreateFunction();
    function.Counters = new PerformanceCounterSetEx(2);
    function.Counters.Add(new PerformanceCounterSetEx.PerformanceCounterValueEx {
      CounterId = 1,
      Label = "85,900 K",
      Value = 85900000
    });
    function.Counters.Add(new PerformanceCounterSetEx.PerformanceCounterValueEx {
      CounterId = 2,
      Label = "1.25",
      Value = 1.25
    });

    var columns = new List<FunctionListExportColumn> {
      new("Function", item => item.Name),
      CounterColumn("TotalCycles", 2),
      CounterColumn("InstrRetired", 1),
      CounterColumn("MissingCounter", 3)
    };

    string markdown = FunctionListExporting.ExportMarkdown(new[] {function}, columns);
    int cyclesIndex = markdown.IndexOf("TotalCycles", StringComparison.Ordinal);
    int instructionsIndex = markdown.IndexOf("InstrRetired", StringComparison.Ordinal);
    Assert.IsTrue(cyclesIndex < instructionsIndex);
    StringAssert.Contains(markdown, "| ExportedFunction | 1.25 | 85,900 K |  |");

    var html = FunctionListExporting.ExportHtml(new[] {function}, columns);
    string htmlText = html.OuterHtml;
    Assert.IsTrue(htmlText.IndexOf("TotalCycles", StringComparison.Ordinal) <
                  htmlText.IndexOf("InstrRetired", StringComparison.Ordinal));
    StringAssert.Contains(htmlText, ">1.25<");
    StringAssert.Contains(htmlText, ">85,900 K<");
  }

  [TestMethod]
  public void PerformanceColumns_ProvideRawNumericExcelValues() {
    var function = CreateFunction();
    function.Counters = new PerformanceCounterSetEx(2);
    function.Counters.Add(new PerformanceCounterSetEx.PerformanceCounterValueEx {
      CounterId = 1,
      Label = "0",
      Value = 0
    });
    function.Counters.Add(new PerformanceCounterSetEx.PerformanceCounterValueEx {
      CounterId = 2,
      Label = "1.25",
      Value = 1.25
    });

    var zeroCounter = CounterColumn("InstrRetired", 1);
    var metric = CounterColumn("CPI", 2);
    var missing = CounterColumn("Missing", 3);

    Assert.AreEqual(0, zeroCounter.NumericValue(function));
    Assert.AreEqual(1.25, metric.NumericValue(function));
    Assert.IsNull(missing.NumericValue(function));
  }

  [TestMethod]
  public void Exporters_KeepStandardOutputWithoutPerformanceColumns() {
    var function = CreateFunction();
    var columns = new List<FunctionListExportColumn> {
      new("Function", item => item.Name),
      new("Module", item => item.ModuleName)
    };

    string markdown = FunctionListExporting.ExportMarkdown(new[] {function}, columns);
    StringAssert.Contains(markdown, "| Function | Module |");
    StringAssert.Contains(markdown, "| ExportedFunction | TestModule.dll |");
    Assert.IsFalse(markdown.Contains("InstrRetired", StringComparison.Ordinal));
  }

  private static FunctionListExportColumn CounterColumn(string header, int counterId) {
    return new FunctionListExportColumn(
      header,
      function => function.Counters?.FindCounterLabel(counterId),
      true,
      function => function.Counters?.FindCounterValueOrNull(counterId),
      true);
  }

  private static IRTextFunctionEx CreateFunction() {
    var summary = new IRTextSummary("TestModule.dll");
    var function = new IRTextFunction("ExportedFunction");
    summary.AddFunction(function);
    return new IRTextFunctionEx(function, 0, null);
  }
}
