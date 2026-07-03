(() => {
    const form = document.querySelector("[data-login-form]");
    const submit = document.querySelector("[data-login-submit]");
    const password = document.querySelector("#Password");
    const togglePassword = document.querySelector("[data-toggle-password]");

    togglePassword?.addEventListener("click", () => {
        if (!(password instanceof HTMLInputElement)) {
            return;
        }

        const visible = password.type === "text";
        password.type = visible ? "password" : "text";
        togglePassword.classList.toggle("is-visible", !visible);
        togglePassword.setAttribute("aria-label", visible ? "显示密码" : "隐藏密码");
    });

    form?.addEventListener("submit", () => {
        if (!(submit instanceof HTMLButtonElement)) {
            return;
        }

        submit.disabled = true;
        const label = submit.querySelector("span");
        const icon = submit.querySelector("strong");

        if (label) {
            label.textContent = "正在验证";
        }

        if (icon) {
            icon.textContent = "…";
        }
    });
})();
