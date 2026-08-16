// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.Text;
using System.Web;
using HtmlAgilityPack;

namespace ProfileExplorer.UI;

public sealed class FunctionListExportColumn {
  public FunctionListExportColumn(string header, Func<IRTextFunctionEx, string> textValue,
                                  bool isProfileValue = false,
                                  Func<IRTextFunctionEx, double?> numericValue = null,
                                  bool isExcelHighlighted = false) {
    Header = header;
    TextValue = textValue;
    IsProfileValue = isProfileValue;
    NumericValue = numericValue;
    IsExcelHighlighted = isExcelHighlighted;
  }

  public string Header { get; }
  public Func<IRTextFunctionEx, string> TextValue { get; }
  public Func<IRTextFunctionEx, double?> NumericValue { get; }
  public bool IsProfileValue { get; }
  public bool IsExcelHighlighted { get; }
  public bool IsNumeric => NumericValue != null;
}

public static class FunctionListExporting {
  public static string ExportMarkdown(IReadOnlyList<IRTextFunctionEx> functions,
                                      IReadOnlyList<FunctionListExportColumn> columns) {
    var sb = new StringBuilder();
    sb.Append('|');

    foreach (var column in columns) {
      sb.Append($" {column.Header} |");
    }

    sb.AppendLine();
    sb.Append('|');

    foreach (var column in columns) {
      sb.Append(new string('-', Math.Max(3, column.Header.Length + 2)));
      sb.Append('|');
    }

    sb.AppendLine();

    foreach (var function in functions) {
      sb.Append('|');

      foreach (var column in columns) {
        sb.Append($" {column.TextValue(function) ?? string.Empty} |");
      }

      sb.AppendLine();
    }

    return sb.ToString();
  }

  public static HtmlNode ExportHtml(IReadOnlyList<IRTextFunctionEx> functions,
                                    IReadOnlyList<FunctionListExportColumn> columns) {
    const string tableStyle = @"border-collapse:collapse;border-spacing:0;";
    const string headerStyle =
      @"background-color:#D3D3D3;white-space:nowrap;text-align:left;vertical-align:top;border-color:black;border-style:solid;border-width:1px;overflow:hidden;padding:2px 2px;font-family:Arial, sans-serif;";
    const string cellStyle =
      @"text-align:left;vertical-align:top;word-wrap:break-word;max-width:300px;overflow:hidden;padding:2px 2px;border-color:black;border-style:solid;border-width:1px;font-family:Arial, sans-serif;";

    var doc = new HtmlDocument();
    var table = doc.CreateElement("table");
    table.SetAttributeValue("style", tableStyle);
    var thead = doc.CreateElement("thead");
    var headerRow = doc.CreateElement("tr");

    foreach (var column in columns) {
      var th = doc.CreateElement("th");
      th.InnerHtml = HttpUtility.HtmlEncode(column.Header);
      th.SetAttributeValue("style", headerStyle);
      headerRow.AppendChild(th);
    }

    thead.AppendChild(headerRow);
    table.AppendChild(thead);
    var tbody = doc.CreateElement("tbody");

    foreach (var function in functions) {
      var row = doc.CreateElement("tr");
      string backColor = Utils.BrushToString(function.BackColor);
      string colorAttr = backColor != null ? $";background-color:{backColor}" : "";

      foreach (var column in columns) {
        var cell = doc.CreateElement("td");
        cell.InnerHtml = HttpUtility.HtmlEncode(column.TextValue(function) ?? string.Empty);
        cell.SetAttributeValue("style", $"{cellStyle}{(column.IsProfileValue ? colorAttr : "")}");
        row.AppendChild(cell);
      }

      tbody.AppendChild(row);
    }

    table.AppendChild(tbody);
    doc.DocumentNode.AppendChild(table);
    return doc.DocumentNode;
  }
}
