﻿using System.Diagnostics;
using System.IO;
using System.Threading;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

// 空白ページのアイテム テンプレートについては、http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409 を参照してください

namespace Miyu {
    /// <summary>
    /// それ自体で使用できる空白ページまたはフレーム内に移動できる空白ページ。
    /// </summary>
    public sealed partial class MainPage : Page {
        public static MainPage theMainPage;
        TProject MainProject;
        public static SynchronizationContext UIContext;

        public MainPage() {
            this.InitializeComponent();

            Debug.WriteLine("メイン開始");

            theMainPage = this;
            UIContext = SynchronizationContext.Current;

            MainProject = new TProject();
            MainProject.Main();

            foreach (TSourceFile src in MainProject.SourceFiles) {
                string file_name = Path.GetFileName(src.PathSrc);
                lst_SourceFiles.Items.Add(file_name);
            }
        }

        private void lst_SourceFiles_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) {
            int idx = lst_SourceFiles.SelectedIndex;

            if (0 <= idx && idx < MainProject.SourceFiles.Count) {
                TSourceFile src = MainProject.SourceFiles[idx];

                MyEditor editor = LeftEditor;

                src.Parser = TCSharpParser.CSharpParser;

                editor.SetSource(src);
                editor.Focus(FocusState.Programmatic);
                editor.InvalidateCanvas();
            }
        }

        /*
         * メインページを再描画する。
         */
        public void InvalidateMainPage() {
            UIContext.Post(state => {

                if (!MainProject.Modified.WaitOne(0) && ! TProject.InBuild) {
                    // ソースが変更されてない場合

                    // ソースの解析後の字句情報を得る。
                    foreach (TSourceFile src in MainProject.SourceFiles) {
                        src.EditLines = src.Lines;
                    }
                }

                LeftEditor.InvalidateCanvas();
                RightEditor.InvalidateCanvas();
            }, null);
        }

        private void MainCanvas_SizeChanged(object sender, SizeChangedEventArgs e) {
            double h = MainCanvas.ActualHeight;
            double h2 = h - OutputPane.ActualHeight;

            lst_SourceFiles.Height  = h;
            LeftEditor.Height       = h2;
            RightEditor.Height      = h2;

            double w = (MainCanvas.ActualWidth - lst_SourceFiles.ActualWidth) / 2;

            LeftEditor.Width    = w;
            RightEditor.Width   = w;
            OutputPane.Width    = 2 * w;

            Canvas.SetLeft(RightEditor, Canvas.GetLeft(LeftEditor) + w);

            Canvas.SetTop(OutputPane, h2);
        }
    }
}
