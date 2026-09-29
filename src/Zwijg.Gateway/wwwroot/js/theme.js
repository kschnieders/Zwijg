// Hell, dunkel oder wie das System. Wird vor dem CSS geladen, damit beim Laden nichts aufblitzt.
(function () {
  var choice = null;
  try { choice = localStorage.getItem("zwijg.theme"); } catch (e) {}
  var dark = choice === "dark" || (choice !== "light" && matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.dataset.theme = dark ? "dark" : "light";
})();
