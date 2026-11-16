# Word, PowerPoint and Excel to PDF

These conversions use the corresponding Microsoft Office **desktop** application
when its COM registration is present. Browser-only Office does not supply this
local automation interface. A working local LibreOffice installation is the
fallback, including when Office is registered but throws or produces no usable PDF.

The app does not request administrator elevation or change Office/Windows security
settings for conversion. Opening Office normally may still be necessary to finish
first-run setup or activation; document-specific protection remains in effect.

## Output validation

Word/PDF/PowerPoint conversions first inspect the input for content. Empty files,
blank DOCX documents, decks with no populated slides, and PDFs containing only
blank pages display `الملف فارغ، لا يوجد محتوى للتحويل.` and do not start a conversion.
The file list marks them in amber as `الملف فارغ`; other batch files continue.
Images, tables, drawings, symbols, header/footer text, and scanned PDF pages are
content even when no ordinary text can be extracted. A mixed PDF with at least
one nonblank page is allowed. Existing output files are retained.

The XML checks support both strict and transitional OOXML. Legacy DOC/PPT/RTF/ODT
inputs are inspected for visible PDF content after their native engine renders
into staging, before the result is published.

The document router gives each job a fresh temporary directory. An Office export
must produce a readable PDF with at least one page before it counts as successful.
If Office fails, LibreOffice receives a different temporary output path so it
cannot accidentally reuse an incomplete Office file. Existing destination files
are replaced only after a new PDF passes validation. Cancellation preserves the
existing export. The success screen displays the destination folder.

LibreOffice uses a separate writable temporary user profile for each job, and
`soffice.com` when available. Its output is checked before being moved to the
requested destination. A zero process exit code by itself is insufficient, and
an old PDF in the destination folder cannot count as a new conversion.

PowerPoint presentations open read-only for PDF export. Process ownership uses
NetOffice's supported window-handle utility, because PowerPoint's obsolete
dispatch `HWND` property can fail on newer Office. Cleanup only terminates
Office processes tracked as newly created by Cortex FX.

## Troubleshooting a friend's computer

1. Open the source in the relevant desktop application and test its own PDF export.
2. In Cortex FX, choose a writable output folder. If Create subfolder is enabled,
   look inside its `Cortex FX` child. The completion dialog displays the saved path.
3. If Office export cannot run, install LibreOffice as an alternative. It is not
   included in the lightweight Cortex FX installer.
4. On failure, use Open logs. Office exceptions retain their nested HRESULTs;
   logs are under `%LOCALAPPDATA%/Cortex FX/Logs`.

Without the failing document and that computer's log, local tests cannot establish
the exact reason for a failure on another computer.

References: [PowerPoint Presentations.Open](https://learn.microsoft.com/en-us/office/vba/api/powerpoint.presentations.open),
[LibreOffice command-line parameters and user profiles](https://help.libreoffice.org/latest/en-US/text/shared/guide/start_parameters.html).
