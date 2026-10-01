// Ładowany synchronicznie w <head>, zanim cokolwiek się narysuje: ustala,
// czy strona gra pełne efekty. Domyślnie idzie za ustawieniem systemu,
// a przełącznik w nawigacji zapisuje wybór użytkownika.
(function () {
  var stored = null;
  try {
    stored = localStorage.getItem("dismode-motion");
  } catch (error) {
    stored = null;
  }
  var systemReduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
  var reduced = stored ? stored === "reduced" : systemReduced;
  document.documentElement.dataset.motion = reduced ? "reduced" : "full";
})();
