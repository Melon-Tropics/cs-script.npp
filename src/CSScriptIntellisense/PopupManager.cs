using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CSScriptIntellisense.Interop;

namespace CSScriptIntellisense
{
    class MemberInfoPopupManager : PopupManager<MemberInfoPanel>
    {
        MouseMonitor hook = new MouseMonitor();

        public MemberInfoPopupManager(Action popupRequest)
            : base(popupRequest)
        {
            PopupRequest = popupRequest;
            hook.MouseMove += hook_MouseMove;
            hook.Install();
        }

        public bool Simple
        {
            get
            {
                return popupForm != null && popupForm.Simple;
            }
        }

        const UInt32 WM_SIZE = 0x0005;
        const UInt32 WM_MOVE = 0x0003;

        bool simple = false;
        int? lastMethodStartPos;
        public void TriggerPopup(bool simple, int methodStartPos, string[] data)
        {
            try
            {
                this.simple = simple;
                lastMethodStartPos = methodStartPos;

                base.Popup(
                    form => //on opening
                    {
                        if (!simple)
                            form.LeftBottomCorner = Npp.GetCaretScreenLocation();
                        else
                            form.LeftBottomCorner = Cursor.Position;

                        form.Simple = simple;
                        if (!simple)
                        {
                            KeyInterceptor.Instance.Add(Keys.Down, Keys.Up, Keys.Escape, Keys.Enter, Keys.Delete, Keys.Back);
                            KeyInterceptor.Instance.KeyDown += Instance_KeyDown;

                            form.KeyPress += (sender, e) =>
                            {
                                NppEditor.ProcessKeyPress(e.KeyChar);
                                CheckIfNeedsClosing();
                            };

                            form.KeyDown += (sender, e) =>
                            {
                                if (e.KeyCode == Keys.Delete)
                                    NppEditor.ProcessDeleteKeyDown();
                            };
                        }
                        form.AddData(data);
                        if (!simple)
                        {
                            form.ProcessMethodOverloadHint(NppEditor.GetMethodOverloadHint(methodStartPos));
                            Task.Factory.StartNew(() =>
                                {
                                    Rectangle rect = Npp.GetClientRect();

                                    while (popupForm != null)
                                    {
                                        Npp.GrabFocus();
                                        var newRect = Npp.GetClientRect();
                                        if (rect != newRect)
                                        {
                                            base.Close();
                                            return;
                                        }
                                        Thread.Sleep(500);
                                    }
                                });
                        }
                    },
                    form => //on closing
                    {
                        if (!simple)
                        {
                            KeyInterceptor.Instance.Remove(Keys.Down, Keys.Up, Keys.Escape, Keys.Enter, Keys.Delete, Keys.Back);
                            KeyInterceptor.Instance.KeyDown -= Instance_KeyDown;
                        }
                    });
            }
            catch { }
        }

        void Instance_KeyDown(Keys key, int repeatCount, ref bool handled)
        {
            if (key == Keys.Down || key == Keys.Up || key == Keys.Enter)
                handled = true;
            try
            {
                popupForm.kbdHook_KeyDown(key, repeatCount);

                CheckIfNeedsClosing();
            }
            catch { }
        }

        public void CheckIfNeedsClosing()
        {
            if (IsShowing && !simple && lastMethodStartPos.HasValue)
            {
                int methodStartPos = lastMethodStartPos.Value;

                string text;
                popupForm.ProcessMethodOverloadHint(NppEditor.GetMethodOverloadHint(methodStartPos, out text));

                int currentPos = Npp.GetCaretPosition();
                if (currentPos <= methodStartPos) //user removed method start
                {
                    Debug.WriteLine("Closing:     currentPos {0} <= methodStartPos {1}", currentPos, methodStartPos);
                    base.Close();
                }
                else if (text != null && text[text.Length - 1] == ')')
                {
                    string typedArgs = text;
                    if (NRefactoryExtensions.AreBracketsClosed(typedArgs))
                    {
                        Debug.WriteLine("Closing:     AreBracketsClosed");
                        base.Close();
                    }
                }
            }
        }
    }

    //it is generic class though so far it is used only for the MemberInfoPanel
    class PopupManager<T> where T : Form, IPopupForm, new()
    {
        public bool Enabled = false;

        public T popupForm;

        protected Action PopupRequest;
        Point lastScheduledPoint;

        public PopupManager(Action popupRequest)
        {
            PopupRequest = popupRequest;
        }

        //public void hook_MouseMove(object sender, MouseHookEventArgs e)
        public void hook_MouseMove()
        {
            if (Enabled)
            {
                if (lastScheduledPoint != Cursor.Position)
                {
                    if (popupForm == null)
                    {
                        lastScheduledPoint = Cursor.Position;
                        Dispatcher.Shedule(700, PopupRequest);
                    }
                    else
                    {
                        if (popupForm.Visible && popupForm.AutoClose)
                            Close();
                    }
                }
            }
            else
            {
                Close();
            }
        }

        public void Close()
        {
            if (popupForm != null && popupForm.Visible)
            {
                popupForm.Close();
                popupForm = null;
            }
        }

        public void Popup(Action<T> init, Action<T> end)
        {
            if (IsShowing)
                popupForm.Close();

            popupForm = new T();
            popupForm.FormClosed += (sender, e) =>
                {
                    end(popupForm);
                    popupForm = null;
                };

            init(popupForm);

            popupForm.Show(owner: Plugin.GetCurrentScintilla());
        }

        public bool IsShowing
        {
            get
            {
                return popupForm != null && popupForm.Visible;
            }
        }
    }

    static class FormExtensions
    {
        public static void Show(this Form form, IntPtr owner)
        {
            var nativeWindow = new NativeWindow();

            nativeWindow.AssignHandle(owner);

            form.Show(nativeWindow);
        }
    }
}