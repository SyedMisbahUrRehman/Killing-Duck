// The first-run disclosure. The duck stays off until the user agrees here.

const agree = document.getElementById("agree");
const note = document.getElementById("consent-note");

function showAgreed() {
  agree.disabled = true;
  agree.textContent = "THE DUCK IS AWAKE";
  note.textContent = "Thanks. Click the duck in your toolbar to see it. You can close this tab.";
}

agree.addEventListener("click", async () => {
  agree.disabled = true;
  const result = await chrome.runtime.sendMessage({ type: "GIVE_CONSENT" });
  if (result?.success) {
    showAgreed();
  } else {
    agree.disabled = false;
    note.textContent = "Something went wrong. Please try again.";
  }
});

chrome.runtime.sendMessage({ type: "GET_DASHBOARD" }).then(state => {
  if (state?.prefs?.consented) showAgreed();
});
