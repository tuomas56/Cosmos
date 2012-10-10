﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.IO;

using Cosmos.Build.Common;

namespace XSharp.Test {
  public partial class MainForm : Form {
    // D:\source\Cosmos\source2\Tests\XSharpCompilerTester\bin\Debug
    // D:\source\Cosmos\source2\Users\Matthijs\MatthijsPlayground

    public MainForm() {
      InitializeComponent();
    }

    internal DirectoryInfo RootDirectory { get; set; }

    protected void Test(string aFilename) {
      tabsMain.TabPages.Add(Path.GetFileNameWithoutExtension(aFilename));
      var xTab = tabsMain.TabPages[tabsMain.TabPages.Count - 1];

      var xTbox = new TextBox();
      xTab.Controls.Add(xTbox);
      xTbox.Dock = DockStyle.Fill;
      xTbox.Multiline = true;
      xTbox.Font = new Font("Consolas", 12);
      xTbox.ScrollBars = ScrollBars.Both;

      using (var xInput = new StreamReader(aFilename)) {
        using (var xOutputCode = new StringWriter()) {
          using (var xOutputData = new StringWriter()) {
            try {
              var xGenerator = new Cosmos.Compiler.XSharp.AsmGenerator();
              xGenerator.Generate(xInput, xOutputData, xOutputCode);

              xTbox.Text = xOutputData.ToString() + "\r\n"
                + xOutputCode.ToString();
            } catch (Exception ex) {
              xTab.Text = "* " + xTab.Text;
              xTbox.Text = xOutputData.ToString() + "\r\n"
                + xOutputCode.ToString() + "\r\n"
                + ex.Message + "\r\n";
            }
          }
        }
      }
    }

    private void MainForm_Load(object sender, EventArgs e) {
      if (null == RootDirectory) { RootDirectory = new DirectoryInfo(CosmosPaths.DebugStubSrc); }
      // For testing
      // Test(Path.Combine(RootDirectory.FullName, "Serial.xs"));

      var xFiles = Directory.GetFiles(RootDirectory.FullName, "*.xs");
      foreach (var xFile in xFiles) {
        Test(xFile);
      }
    }
  }
}
