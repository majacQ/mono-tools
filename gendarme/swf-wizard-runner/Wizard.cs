//
// Gendarme.cs: A SWF-based Wizard Runner for Gendarme
//
// Authors:
//	Sebastien Pouliot <sebastien@ximian.com>
//
// Copyright (C) 2008 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

using Gendarme.Framework;

using Mono.Cecil;

namespace Gendarme {

	public partial class Wizard : Form {

		// used to call code asynchronously
		delegate void MethodInvoker ();

		public enum Page {
			Welcome,
			AddFiles,
			SelectRules,
			Analyze,
			Report
		}

		class AssemblyInfo {
			public DateTime Timestamp;
			public AssemblyDefinition Definition;
		}

		private const string BaseUrl = "http://www.mono-project.com/";
		private const string DefaultUrl = BaseUrl + "Gendarme";

		static Process process;

		private bool rules_populated;
		private Dictionary<string, AssemblyInfo> assemblies;
		private GuiRunner runner;
		private int counter;

		private MethodInvoker assembly_loader;
		private IAsyncResult assemblies_loading;

		private MethodInvoker rule_loader;
		private IAsyncResult rules_loading;

		private MethodInvoker analyze;
		private IAsyncResult analyzing;

		private string html_report_filename;
		private string xml_report_filename;
		private string text_report_filename;


		public Wizard ()
		{
			InitializeComponent ();
			// hide the tabs from the TabControl/TabPage[s] being [mis-]used
			// to implement this wizard
			wizard_tab_control.Top = -22;

			welcome_link_label.Text = DefaultUrl;
			welcome_wizard_label.Text = String.Format ("Gendarme Wizard Runner Version {0}",
				GetVersion (GetType ()));
			welcome_framework_label.Text = String.Format ("Gendarme Framework Version {0}",
				GetVersion (typeof (IRule)));

			assembly_loader = UpdateAssemblies;

			UpdatePageUI ();
		}

		private static Version GetVersion (Type type)
		{
			return type.Assembly.GetName ().Version;
		}

		static void EndCallback (IAsyncResult result)
		{
			(result.AsyncState as MethodInvoker).EndInvoke (result);
		}

		static Process Process {
			get {
				if (process == null)
					process = new Process ();
				return process;
			}
		}

		static void Open (string filename)
		{
			Process.StartInfo.Verb = "open";
			Process.StartInfo.FileName = filename;
			Process.Start ();
		}

		#region general wizard code

		public Page Current {
			get { return (Page) wizard_tab_control.SelectedIndex; }
			set {
				wizard_tab_control.SelectedIndex = (int) value;
				UpdatePageUI ();
			}
		}

		public GuiRunner Runner {
			get {
				if (runner == null)
					runner = new GuiRunner (this);
				return runner;
			}
		}

		private void BackButtonClick (object sender, EventArgs e)
		{
			switch (Current) {
			case Page.Welcome:
				return;
			case Page.AddFiles:
				Current = Page.Welcome;
				break;
			case Page.SelectRules:
				Current = Page.AddFiles;
				break;
			case Page.Analyze:
				// then ask confirmation before aborting 
				// and move back one step
				if (ConfirmAnalyzeAbort (false))
					Current = Page.SelectRules;
				break;
			case Page.Report:
				// move two step back (i.e. skip analyze)
				Current = Page.SelectRules;
				break;
			}
		}

		private void NextButtonClick (object sender, EventArgs e)
		{
			switch (Current) {
			case Page.Welcome:
				Current = Page.AddFiles;
				break;
			case Page.AddFiles:
				Current = Page.SelectRules;
				break;
			case Page.SelectRules:
				Current = Page.Analyze;
				break;
			case Page.Analyze:
			case Page.Report:
				// should not happen
				return;
			}
		}

		private void CancelButtonClick (object sender, EventArgs e)
		{
			// if we're analyzing...
			if (Current == Page.Analyze) {
				// then ask confirmation before aborting
				if (!ConfirmAnalyzeAbort (true))
					return;
			}
			Close ();
		}

		private void HelpButtonClick (object sender, EventArgs e)
		{
			// open web browser to http://www.mono-project.com/Gendarme
			Open (DefaultUrl);
		}

		private void UpdatePageUI ()
		{
			back_button.Enabled = true;
			next_button.Enabled = true;
			cancel_button.Text = "Cancel";

			switch (Current) {
			case Page.Welcome:
				UpdateWelcomeUI ();
				break;
			case Page.AddFiles:
				UpdateAddFilesUI ();
				break;
			case Page.SelectRules:
				UpdateSelectRulesUI ();
				break;
			case Page.Analyze:
				UpdateAnalyzeUI ();
				break;
			case Page.Report:
				UpdateReportUI ();
				break;
			}
		}

		#endregion

		#region Welcome

		private void UpdateWelcomeUI ()
		{
			back_button.Enabled = false;
			if (rule_loader == null) {
				rule_loader = Runner.LoadRules;
				rules_loading = rule_loader.BeginInvoke (EndCallback, rule_loader);
			}
		}

		private void GendarmeLinkClick (object sender, LinkLabelLinkClickedEventArgs e)
		{
			Open (DefaultUrl);
		}

		#endregion

		#region Add Files

		private void UpdateAddFilesUI ()
		{
			if (assemblies == null)
				assemblies = new Dictionary<string, AssemblyInfo> ();

			int files_count = file_list_box.Items.Count;
			bool has_files = (files_count > 0);
			next_button.Enabled = has_files;
			remove_file_button.Enabled = has_files;
			if (has_files) {
				add_files_count_label.Text = String.Format ("{0} assembl{1} selected",
					files_count, files_count == 1 ? "y" : "ies");
			} else {
				add_files_count_label.Text = "No assembly selected.";
			}
		}

		private void AddFilesButtonClick (object sender, EventArgs e)
		{
			if (open_file_dialog.ShowDialog (this) == DialogResult.OK) {
				foreach (string filename in open_file_dialog.FileNames) {
					// don't add duplicates
					if (!file_list_box.Items.Contains (filename)) {
						file_list_box.Items.Add (filename);
						assemblies.Add (filename, new AssemblyInfo ());
					}
				}
			}
			UpdatePageUI ();
		}

		private void RemoveFileButtonClick (object sender, EventArgs e)
		{
			// remove from the last one to the first one 
			// so the indices are still valid during the removal operation
			for (int i = file_list_box.SelectedIndices.Count - 1; i >= 0; i--) {
				int remove = file_list_box.SelectedIndices [i];

				// if some AssemblyDefinition are already loaded...
				if (assemblies != null) {
					// then look if we need to remove them too!
					assemblies.Remove ((string) file_list_box.Items [remove]);
				}
				file_list_box.Items.RemoveAt (remove);
			}
			UpdatePageUI ();
		}

		public void UpdateAssemblies ()
		{
			foreach (KeyValuePair<string,AssemblyInfo> kvp in assemblies) {
				DateTime last_write = File.GetLastWriteTimeUtc (kvp.Key);
				if ((kvp.Value.Definition == null) || (kvp.Value.Timestamp < last_write)) {
					AssemblyInfo a = kvp.Value;
					a.Timestamp = last_write;
					a.Definition = AssemblyFactory.GetAssembly (kvp.Key);
				}
			}
		}

		#endregion

		#region Select Rules

		private void UpdateSelectRulesUI ()
		{
			// asynchronously load assemblies (or the one that changed)
			assemblies_loading = assembly_loader.BeginInvoke (EndCallback, assembly_loader);

			rules_count_label.Text = String.Format ("{0} rules are available.", Runner.Rules.Count);
			if (rules_loading == null)
				throw new InvalidOperationException ("rules_loading");
			next_button.Enabled = rules_loading.IsCompleted;
			rules_tree_view.Enabled = rules_loading.IsCompleted;
			rules_loading.AsyncWaitHandle.WaitOne ();
			PopulateRules ();
		}

		private void PopulateRules ()
		{
			if (rules_populated)
				return;

			Dictionary<string, TreeNode> nodes = new Dictionary<string, TreeNode> ();

			rules_tree_view.BeginUpdate ();
			foreach (IRule rule in Runner.Rules) {
				TreeNode parent;
				string name_space = rule.FullName.Substring (0, rule.FullName.Length - rule.Name.Length - 1);
				if (!nodes.TryGetValue (name_space, out parent)) {
					parent = new TreeNode (name_space);
					parent.Checked = true;
					nodes.Add (name_space, parent);
					rules_tree_view.Nodes.Add (parent);
				}

				TreeNode node = new TreeNode (rule.Name);
				node.Checked = true;
				node.Tag = rule;
				node.ToolTipText = rule.Problem;
				parent.Nodes.Add (node);
			}
			foreach (TreeNode node in rules_tree_view.Nodes) {
				node.ToolTipText = String.Format ("{0} rules available", node.Nodes.Count);
			}
			nodes.Clear ();

			rules_tree_view.Sort ();
			rules_tree_view.EndUpdate ();
			rules_populated = true;
			UpdatePageUI ();
		}

		private void BrowseDocumentationButtonClick (object sender, EventArgs e)
		{
			string url = null;

			if (rules_tree_view.SelectedNode == null)
				url = DefaultUrl;
			else {
				if (rules_tree_view.SelectedNode.Tag == null) {
					url = BaseUrl + rules_tree_view.SelectedNode.Text;
				} else {
					url = (rules_tree_view.SelectedNode.Tag as IRule).Uri.ToString ();
				}
			}

			Open (url);
		}

		private void RulesTreeViewAfterCheck (object sender, TreeViewEventArgs e)
		{
			IRule rule = (e.Node.Tag as IRule);
			if (rule == null) {
				// childs
				foreach (TreeNode node in e.Node.Nodes) {
					node.Checked = e.Node.Checked;
				}
			} else {
				rule.Active = e.Node.Checked;
			}
		}

		#endregion

		#region Analyze

		private void UpdateAnalyzeUI ()
		{
			// update UI before waiting for assemblies to be loaded
			progress_bar.Value = 0;
			next_button.Enabled = false;
			analyze_status_label.Text = String.Format ("Processing assembly 1 of {0}",
				assemblies.Count);
			analyze_defect_label.Text = String.Format ("Defects Found: 0");
			// make sure all assemblies are loaded into memory
			assemblies_loading.AsyncWaitHandle.WaitOne ();
			PrepareAnalyze ();
			analyze = Analyze;
			analyzing = analyze.BeginInvoke (EndCallback, analyze);
		}

		private void PrepareAnalyze ()
		{
			// any existing report is now out-of-date
			html_report_filename = null;
			xml_report_filename = null;
			text_report_filename = null;

			// just to pick up any change between the original load (a few steps bacl)
			// and the assemblies "right now" sitting on disk
			UpdateAssemblies ();

			Runner.Reset ();
			Runner.Assemblies.Clear ();
			foreach (KeyValuePair<string, AssemblyInfo> kvp in assemblies) {
				// add assemblies references to runner
				Runner.Assemblies.Add (kvp.Value.Definition);
			}

			progress_bar.Maximum = Runner.Assemblies.Count;
		}

		private void Analyze ()
		{
			counter = 0;
			Runner.Initialize ();
			Runner.Run ();

			BeginInvoke ((Action) (() => Current = Page.Report));
		}

		private bool ConfirmAnalyzeAbort (bool quit)
		{
			string message = String.Format ("Abort the current analysis being executed {0}Gendarme ?",
				quit ? "and quit " : String.Empty);
			return (MessageBox.Show (this, message, "Gendarme", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
				MessageBoxDefaultButton.Button2) == DialogResult.Yes);
		}

		/// <summary>
		/// Update UI before analyzing an assembly.
		/// </summary>
		/// <param name="e">RunnerEventArgs that contains the Assembly being analyzed and the Runner</param>
		public void PreAssemblyUpdate (RunnerEventArgs e)
		{
			progress_bar.Value = counter++;
			analyze_status_label.Text = String.Format ("Processing assembly {0} of {1}",
				counter, e.Runner.Assemblies.Count);
			analyze_assembly_label.Text = "Assembly: " + e.CurrentAssembly.Name.FullName;
		}

		/// <summary>
		/// Update UI after analyzing an assembly.
		/// </summary>
		/// <param name="e">RunnerEventArgs that contains the Assembly being analyzed and the Runner</param>
		public void PostTypeUpdate (RunnerEventArgs e)
		{
			analyze_defect_label.Text = String.Format ("Defects Found: {0}", e.Runner.Defects.Count);
		}

		#endregion

		#region Report

		private void UpdateReportUI ()
		{
			bool has_defects = (Runner.Defects.Count > 0);
			save_report_button.Enabled = has_defects;
			view_report_button.Enabled = has_defects;
			report_subtitle_label.Text = String.Format ("Gendarme has found {0} defects during analysis.",
				has_defects ? Runner.Defects.Count.ToString () : "no");
			cancel_button.Text = "Close";
			next_button.Enabled = false;
		}

		private bool CouldCopyReport (ref string currentName, string fileName)
		{
			// if possible avoid re-creating the report (as it can 
			// be a long operation) and simply copy the file
			bool copy = (currentName != null);
			if (copy)
				File.Copy (currentName, fileName);

			currentName = fileName;
			return copy;
		}

		private void SaveReportButtonClick (object sender, EventArgs e)
		{
			if (save_file_dialog.ShowDialog () != DialogResult.OK)
				return;

			string filename = save_file_dialog.FileName;
			ResultWriter writer = null;

			switch (save_file_dialog.FilterIndex) {
			case 1:
				if (CouldCopyReport (ref html_report_filename, filename))
					return;

				writer = new HtmlResultWriter (Runner, filename);
				break;
			case 2:
				if (CouldCopyReport (ref xml_report_filename, filename))
					return;

				writer = new XmlResultWriter (Runner, filename);
				break;
			case 3:
				if (CouldCopyReport (ref text_report_filename, filename))
					return;

				writer = new TextResultWriter (Runner, filename);
				break;
			}

			if (writer != null) {
				writer.Report ();
				writer.Dispose ();
			}
		}

		private void ViewReportButtonClick (object sender, EventArgs e)
		{
			// open web browser on html report
			if (html_report_filename == null) {
				html_report_filename = Path.ChangeExtension (Path.GetTempFileName (), ".html");
				using (HtmlResultWriter writer = new HtmlResultWriter (Runner, html_report_filename)) {
					writer.Report ();
				}
			}
			Open (html_report_filename);
		}

		#endregion
	}
}
