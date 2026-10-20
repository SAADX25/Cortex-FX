# Document conversion in the floating wheel

- Word-compatible inputs (`DOC`, `DOCX`, `ODT`, `RTF`, `TXT`) use installed Microsoft Word for PDF, DOCX, ODT, RTF, and UTF-8 TXT output, preserving Arabic text. LibreOffice is used when Word is unavailable and LibreOffice is installed.
- PDF to DOCX uses Microsoft Word and validates that the result contains editable text. Image-only/scanned pages need the optional Tesseract OCR engine and English/Arabic language data. Word and PowerPoint are both required for the PDF/Word/PowerPoint bridge.
- EPUB, MOBI, and AZW3 require the optional Calibre engine. Archive conversion requires 7-Zip. Optional tools are detected in the existing bundled/installation locations and in absolute PATH directories.
- The router and radial catalog share availability checks. The floating wheel hides formats and actions whose requirements are unavailable. Visible actions are redistributed around the circle and remain mapped to the correct output format. The router still blocks missing engines if an unavailable conversion is requested elsewhere. Actual conversion failures retain the error indicator and identify the selected output format.

The application does not download or install these optional engines automatically. PDF image exports do not require Microsoft Office, LibreOffice, or Calibre.
