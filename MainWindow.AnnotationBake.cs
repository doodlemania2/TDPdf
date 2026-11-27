// MainWindow — baking annotations into the saved PDF.
// The burn itself lives in Services/AnnotationBaker.cs.

using System.Windows;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        // ============================================================
        // Save annotations to PDF
        // ============================================================

        /// <summary>
        /// Drops all pending form-field values for the active document. Called when the
        /// document object is swapped/reloaded in place (structural edit, undo/redo of a
        /// document change, or open into the current context) because the values are keyed
        /// by field object number, which is invalidated by a reload.
        /// </summary>
        private void ClearFormState()
        {
            _formTextValues.Clear();
            _formCheckValues.Clear();
            _formRadioValues.Clear();
        }

        /// <summary>True if the user has entered any interactive form-field values pending save.</summary>
        private bool HasPendingFormValues =>
            _formTextValues.Count > 0 || _formCheckValues.Count > 0 || _formRadioValues.Count > 0;

        /// <summary>
        /// Writes pending form values into the AcroForm, then burns every pending annotation into
        /// the page content. The burn itself is <see cref="AnnotationBaker.Bake"/>; this keeps the
        /// window-only parts (form values, the failure report, the WPF text measurements).
        /// </summary>
        private void DrawAnnotationsOnDocument()
        {
            if (_doc is null) return;

            // Persist interactive form-field values into the AcroForm before baking
            // annotations, so filled fields are saved alongside drawn annotations.
            WriteFormValuesToDocument();
            // Named to the user once the save in progress has finished, not mid-write.
            if (_formWriteFailures.Count > 0)
                Dispatcher.BeginInvoke(new Action(ReportFormWriteFailures), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var text = new AnnotationTextLayout(
                ta => { var sz = MeasureTextAnnotation(ta); return (sz.Width, sz.Height); },
                (ta, maxWidth) => WrapTextToWidth(ta.Content, ta.FontSize, maxWidth,
                                                  TextLayoutTypeface(ta), ta.LetterSpacing));
            AnnotationBaker.Bake(_doc, _annotations, EnsureRenderDims, text);
        }
    }
}
