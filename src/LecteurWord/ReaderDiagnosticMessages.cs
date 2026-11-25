namespace LecteurWord;

public sealed partial class ReaderWindow
{
    static readonly Dictionary<string, string> EnglishDiagnosticMessages = new(StringComparer.Ordinal)
    {
        ["EXPORT_EPUB_AUXILIARY_ONCE"] = "Relevant auxiliary content appears once in reading order, without recurring page numbers.",
        ["EXPORT_EPUB_REFLOW_BREAK"] = "A page or column break was adapted for continuous reading.",
        ["EXPORT_EPUB_TAB_STOPS"] = "Tab spacing is preserved. The reading system adapts fixed tab positions.",
        ["EXPORT_FIELD_VALUE_SNAPSHOT"] = "The cross-reference keeps its destination and original label as a stored value.",
        ["EXPORT_IMAGE_ALT"] = "The source supplies no description for this EPUB image.",
        ["EXPORT_LIST_LABEL_LITERAL"] = "The list retains its structure and its label remains editable text.",
        ["EXPORT_LIST_UNSUPPORTED"] = "The source label code stays editable. Its appearance depends on font availability.",
        ["EXPORT_NOVA_AUXILIARY"] = "Header or footer content remains editable in the manuscript appendix.",
        ["EXPORT_NOVA_BOOKMARK"] = "This part of the NOVA project cannot represent the bookmark.",
        ["EXPORT_NOVA_BOOKMARK_END"] = "A bookmark without a resolved end is retained as a point anchor.",
        ["EXPORT_NOVA_CELL_BORDER"] = "An individual cell border uses the table style.",
        ["EXPORT_NOVA_CONTEXTUAL_SPACING"] = "Paragraph spacing is fixed using the surrounding DOCX paragraphs.",
        ["EXPORT_NOVA_IMAGE_ANCHOR"] = "The image uses a dedicated paragraph at its position in the flow.",
        ["EXPORT_NOVA_IMAGE_IN_AUXILIARY"] = "The image appears in the appendix with a reference from its source note or cell.",
        ["EXPORT_NOVA_IMAGE_SIZE"] = "The image is scaled proportionally to fit the available area.",
        ["EXPORT_NOVA_INLINE"] = "The text remains editable with adapted spacing, direction or internal breaks.",
        ["EXPORT_NOVA_LANGUAGE"] = "The text is preserved. Its proofing language exceeds the NOVA profile.",
        ["EXPORT_NOVA_LINE_SPACING"] = "Line spacing is converted to the relative values supported by OpusNova.",
        ["EXPORT_NOVA_LINK_IN_AUXILIARY"] = "The link label stays in the source note or cell.",
        ["EXPORT_NOVA_LIST"] = "The list label becomes editable text with a fixed number.",
        ["EXPORT_NOVA_METADATA_CONVERSION"] = "The project keeps the title, author and dates. Subject, keywords and description remain in the DOCX.",
        ["EXPORT_NOVA_NESTED_NOTE"] = "The note body appears as editable text at the reference.",
        ["EXPORT_NOVA_NOTE_RENUMBERED"] = "The note body and reference are preserved with OpusNova numbering.",
        ["EXPORT_NOVA_NOTE_TECHNICAL_MARK"] = "The note retains its native anchor. An unreferenced technical marker was omitted.",
        ["EXPORT_NOVA_PARAGRAPH_STYLE"] = "The text is preserved with adapted border, fill, spacing or indentation.",
        ["EXPORT_NOVA_SECTION_FORMAT"] = "The section uses the project's common geometry and numbering.",
        ["EXPORT_NOVA_SECTION_TRANSITION"] = "The section transition becomes a page break.",
        ["EXPORT_NOVA_TABLE_BORDER"] = "The NOVA profile uses consistent borders across the table.",
        ["EXPORT_NOVA_TABLE_GRID"] = "Empty cells represent unoccupied positions in the Word grid.",
        ["EXPORT_NOVA_TABLE_LAYOUT"] = "The grid and merges remain editable with adapted widths, headers or alignment.",
        ["EXPORT_NOVA_TABLE_PADDING"] = "Cell padding is standardized.",
        ["EXPORT_NOVA_TABLE_ROW_LAYOUT"] = "OpusNova preserves each row as one block when recalculating pages.",
        ["EXPORT_NOVA_TABS"] = "Tabs remain in the text with adapted stops or leaders.",
        ["EXPORT_NOVA_TOC_MARK"] = "The table of contents title and stored result remain readable. The Word field is static.",
        ["EXPORT_NOVA_TRAILING_TABS"] = "Trailing filler tabs become editable spaces.",
        ["EXPORT_OBJECT_FLOW"] = "The object moves closer to its anchor in the reading order. Wrapping may change.",
        ["EXPORT_OBJECT_RASTER"] = "A graphic preserves the appearance of this object and replaces its editable components.",
        ["EXPORT_ODT_INLINE_PAGE_BREAK"] = "An inline page break becomes a line break in the editable paragraph.",
        ["EXPORT_ODT_RUNNING_FRAME_PAGE_FIELD"] = "The dynamic page field moves outside the text frame within the header or footer.",
        ["EXPORT_ODT_SECTION_KIND"] = "Section properties are preserved with a page break at the transition.",
        ["EXPORT_PDF_SYMBOL_VISUAL"] = "The PDF displays the character using the reader's selection or glyph diagnosis.",
        ["EXPORT_PDF_UNRESOLVED_GLYPHS"] = "The PDF retains visible markers for unresolved glyphs.",
        ["EXPORT_REFLOW_AUXILIARY_REPEAT"] = "Another variant with the same role already represents this content and its styles.",
        ["EXPORT_REFLOW_FOLIO"] = "The reflowable edition omits the printed page number.",
        ["EXPORT_REFLOW_PAPER"] = "The publication adapts to the reading device.",
        ["EXPORT_STYLE_UNSUPPORTED"] = "Text remains editable with adapted character effects.",
        ["EXPORT_SYMBOL_SOURCE_CODE"] = "The original character is preserved with its declared font face.",
        ["EXPORT_TEXT_FRAME_FLOW"] = "Text and blocks remain editable in text-frame order with anchoring adapted to the flow.",
        ["EXPORT_TOC_RETAINED_ENTRIES"] = "The edition retains table of contents entries and recalculates navigation from headings.",
        ["EXPORT_UNKNOWN_CONTENT"] = "The representation of this element remains unqualified. See the source DOCX.",
        ["READ_TABLE_GRID_FALLBACK"] = "Fallback grid. Some dimensions or graphic properties remain unqualified.",
        ["TEXT_STORY_UNQUALIFIED"] = "Text outside a qualified Word paragraph is preserved with partial representation."
    };

    static readonly Dictionary<string, string> FrenchDiagnosticMessages = new(StringComparer.Ordinal)
    {
        ["ATTRIBUTE_SEMANTICS_UNQUALIFIED"] = "Attribut XML conservé. Sa sémantique reste à qualifier.",
        ["BINARY_NOT_INTERPRETED"] = "Empreinte de la ressource binaire calculée. Son contenu reste opaque.",
        ["DUPLICATE_STYLE"] = "Identifiant de style ambigu.",
        ["ELEMENT_SEMANTICS_UNQUALIFIED"] = "Structure XML conservée. Sa sémantique reste à qualifier.",
        ["EXTERNAL_NOT_FETCHED"] = "Relation externe recensée dans le document.",
        ["FIDELITY_NOT_QUALIFIED"] = "Inventaire terminé. La disposition Word, le mappage d’import et la validité du schéma OOXML restent à qualifier.",
        ["IMPORT_POLICY_REQUIRED"] = "Révision ou champ préservé. Une politique d’import explicite est nécessaire.",
        ["MEDIA_QUARANTINED"] = "Signature de média incohérente ou hors profil. La ressource a été isolée.",
        ["MISSING_STYLE"] = "Le style référencé est absent."
    };

    string DiagnosticText(string code, string source) =>
        activeTranslation?.Messages.GetValueOrDefault("diagnostic." + code.ToLowerInvariant()) ??
        (english ? EnglishDiagnosticMessages : FrenchDiagnosticMessages).GetValueOrDefault(code) ?? source;
}
