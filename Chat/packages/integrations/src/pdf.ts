import {
  PDFArray,
  PDFCheckBox,
  PDFDict,
  PDFDocument,
  PDFDropdown,
  type PDFField,
  PDFHexString,
  PDFName,
  type PDFObject,
  PDFOptionList,
  PDFRadioGroup,
  PDFString,
  PDFTextField,
  rgb,
  StandardFonts,
} from "pdf-lib";

const MAX_PDF_BYTES = 10 * 1024 * 1024;
const MAX_PDF_PAGES = 500;

export class PdfError extends Error {
  readonly status = 422;
  constructor(message: string) {
    super(message);
    this.name = "PdfError";
  }
}

async function pdfOperation<T>(operation: () => Promise<T>, fallback: string): Promise<T> {
  try {
    return await operation();
  } catch (error) {
    if (error instanceof PdfError) throw error;
    throw new PdfError(fallback);
  }
}

export interface PdfInspection {
  pageCount: number;
  fields: { name: string; value: string; type: "text" | "checkbox" | "unsupported" }[];
}

async function loadPdf(bytes: Uint8Array): Promise<PDFDocument> {
  if (
    !bytes.length ||
    bytes.length > MAX_PDF_BYTES ||
    Buffer.from(bytes.subarray(0, 1024)).indexOf("%PDF-") < 0
  ) {
    throw new PdfError("Invalid PDF: expected nonempty PDF bytes up to 10 MiB");
  }
  try {
    const doc = await PDFDocument.load(new Uint8Array(bytes), {
      ignoreEncryption: false,
      throwOnInvalidObject: true,
      updateMetadata: false,
    });
    if (doc.isEncrypted) throw new PdfError("Encrypted PDFs are not supported");
    if (doc.catalog.lookupMaybe(PDFName.of("AcroForm"), PDFDict)?.has(PDFName.of("XFA"))) {
      throw new PdfError("Unsupported XFA PDF form");
    }
    if (doc.getPageCount() < 1 || doc.getPageCount() > MAX_PDF_PAGES)
      throw new PdfError("PDF must contain between 1 and 500 pages");
    return doc;
  } catch (error) {
    if (error instanceof PdfError) throw error;
    if (error instanceof Error && /encrypt/i.test(error.message))
      throw new PdfError("Encrypted PDFs are not supported");
    throw new PdfError("Invalid or malformed PDF");
  }
}

function inspectField(field: PDFField): PdfInspection["fields"][number] {
  const name = field.getName();
  if (field instanceof PDFTextField) {
    const value = field.acroField.dict.lookup(PDFName.of("V"));
    if (value !== undefined && !(value instanceof PDFString) && !(value instanceof PDFHexString)) {
      throw new PdfError("Cannot inspect PDF: a text field contains a malformed value");
    }
    return { name, value: field.getText() ?? "", type: "text" };
  }
  if (field instanceof PDFCheckBox)
    return { name, value: String(field.isChecked()), type: "checkbox" };
  const value =
    field instanceof PDFDropdown || field instanceof PDFOptionList
      ? field.getSelected().join(", ")
      : field instanceof PDFRadioGroup
        ? (field.getSelected() ?? "")
        : "";
  return { name, value, type: "unsupported" };
}

export async function inspectPdf(bytes: Uint8Array): Promise<PdfInspection> {
  return pdfOperation(async () => {
    const doc = await loadPdf(bytes);
    return { pageCount: doc.getPageCount(), fields: doc.getForm().getFields().map(inspectField) };
  }, "Cannot inspect PDF: the document contains malformed or unsupported form fields");
}

/** Strip action entry points in the new output; never execute PDF scripts. */
function removeActions(doc: PDFDocument): void {
  const visited = new Set<PDFObject>();
  const visit = (object: PDFObject): void => {
    if (visited.has(object)) return;
    visited.add(object);
    if (object instanceof PDFDict) {
      for (const key of ["OpenAction", "AA", "A", "JS", "JavaScript"])
        object.delete(PDFName.of(key));
      for (const value of object.values()) visit(value);
    } else if (object instanceof PDFArray) {
      for (const value of object.asArray()) visit(value);
    }
  };
  for (const [, object] of doc.context.enumerateIndirectObjects()) visit(object);
}

export async function fillPdf(
  bytes: Uint8Array,
  values: Record<string, string | boolean>,
): Promise<Uint8Array> {
  return pdfOperation(async () => {
    const doc = await loadPdf(bytes);
    const form = doc.getForm();
    const fields = new Map(form.getFields().map((field) => [field.getName(), field]));
    for (const [name, value] of Object.entries(values)) {
      const field = fields.get(name);
      if (!field) throw new PdfError(`Unknown PDF field: ${name}`);
      if (field instanceof PDFTextField) {
        if (typeof value !== "string")
          throw new PdfError(`PDF text field requires a string: ${name}`);
        if (value.length > 10000) throw new PdfError(`PDF text field value is too long: ${name}`);
        field.setText(value);
      } else if (field instanceof PDFCheckBox) {
        if (typeof value !== "boolean")
          throw new PdfError(`PDF checkbox requires a boolean: ${name}`);
        if (value) field.check();
        else field.uncheck();
      } else {
        throw new PdfError(`Unsupported PDF field: ${name}`);
      }
    }
    removeActions(doc);
    try {
      form.updateFieldAppearances(await doc.embedFont(StandardFonts.Helvetica));
      return await doc.save();
    } catch {
      throw new PdfError(
        "Could not render PDF field values; use characters supported by this form's font or choose a compatible PDF form",
      );
    }
  }, "Could not fill PDF: the document contains malformed or unsupported form fields");
}

/** Original synthetic fixture. Personal details remain blank until explicitly supplied. */
export async function createSamplePdf(): Promise<Uint8Array> {
  return pdfOperation(async () => {
    const doc = await PDFDocument.create();
    doc.setTitle("Community visit - permission form");
    doc.setAuthor("AI Studio");
    const regular = await doc.embedFont(StandardFonts.Helvetica);
    const bold = await doc.embedFont(StandardFonts.HelveticaBold);
    const ink = rgb(0.12, 0.19, 0.2);
    const teal = rgb(0.18, 0.4, 0.4);
    const form = doc.getForm();
    const pages = [doc.addPage([612, 792]), doc.addPage([612, 792])];
    for (const [index, page] of pages.entries()) {
      page.drawRectangle({ x: 0, y: 682, width: 612, height: 110, color: rgb(0.9, 0.95, 0.93) });
      page.drawText("WORKSPACE / COMMUNITY VISIT", {
        x: 48,
        y: 750,
        size: 10,
        font: bold,
        color: teal,
      });
      page.drawText(index === 0 ? "Community visit" : "Permission & contacts", {
        x: 48,
        y: 710,
        size: 28,
        font: bold,
        color: ink,
      });
      page.drawLine({
        start: { x: 48, y: 55 },
        end: { x: 564, y: 55 },
        color: rgb(0.8, 0.85, 0.83),
        thickness: 1,
      });
      page.drawText("EXAMPLE FORM - no real participant or organization", {
        x: 48,
        y: 35,
        size: 9,
        font: regular,
        color: teal,
      });
      page.drawText(`${index + 1} / 2`, { x: 535, y: 35, size: 9, font: regular, color: teal });
    }
    const first = pages[0];
    const second = pages[1];
    first.drawText("A day of discovery at the community museum", {
      x: 48,
      y: 640,
      size: 16,
      font: bold,
      color: ink,
    });
    const details = [
      "Please complete both pages of this permission form.",
      "The visit, organization, and schedule below are fictional.",
      "Schedule: Saturday, October 10, 2026 / 10:00-14:00",
      "Activities: guided exhibits, a sketching workshop, and a lunch break.",
      "Bring: a packed lunch, water, and comfortable shoes.",
    ];
    details.forEach((line, index) => {
      first.drawText(line, { x: 48, y: 606 - index * 25, size: 11, font: regular, color: ink });
    });
    const addText = (
      page: typeof first,
      name: string,
      label: string,
      y: number,
      multiline = false,
    ) => {
      page.drawText(label, {
        x: 48,
        y: y + (multiline ? 72 : 40),
        size: 11,
        font: bold,
        color: ink,
      });
      const field = form.createTextField(name);
      if (multiline) field.enableMultiline();
      field.addToPage(page, {
        x: 48,
        y,
        width: 516,
        height: multiline ? 62 : 30,
        borderWidth: 1,
        borderColor: rgb(0.65, 0.75, 0.71),
        backgroundColor: rgb(1, 1, 1),
        font: regular,
      });
      field.setFontSize(12);
    };
    addText(first, "participant_name", "Participant name", 395);
    addText(first, "guardian_name", "Parent or guardian name (if applicable)", 305);
    first.drawText("Continue to page 2 for emergency contacts and permission.", {
      x: 48,
      y: 210,
      size: 11,
      font: regular,
      color: teal,
    });
    addText(second, "emergency_contact", "Emergency contact name", 580);
    addText(second, "emergency_phone", "Emergency contact phone", 490);
    addText(second, "additional_notes", "Additional notes (optional)", 375, true);
    const permission = form.createCheckBox("permission_granted");
    permission.addToPage(second, {
      x: 48,
      y: 290,
      width: 18,
      height: 18,
      borderWidth: 1,
      borderColor: teal,
    });
    second.drawText("I give permission for the participant to attend the community visit.", {
      x: 78,
      y: 295,
      size: 11,
      font: regular,
      color: ink,
    });
    second.drawText("This checkbox records permission; it is not a digital signature.", {
      x: 48,
      y: 235,
      size: 10,
      font: regular,
      color: teal,
    });
    form.updateFieldAppearances(regular);
    return doc.save();
  }, "Could not create the sample PDF");
}
