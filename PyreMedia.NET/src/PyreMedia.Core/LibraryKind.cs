namespace PyreMedia.Core;

/// <summary>
/// The four libraries finished media can be moved into.
///
/// Deliberately not the same type as <c>Organizing.MediaKind</c>, which says
/// what a scanned file turned out to be. This says where a finished one lives,
/// and the two do not line up: a scanner has no reason to know about
/// audiobooks, and a destination has no reason to care whether an episode was
/// identified from its filename or its folder.
///
/// Four rather than one because they are not interchangeable. Kodi scans a film
/// library and a television library separately, and a book filed among the
/// albums is a book nobody finds again.
/// </summary>
public enum LibraryKind
{
    Movie,
    Tv,
    Music,
    Audiobook
}
