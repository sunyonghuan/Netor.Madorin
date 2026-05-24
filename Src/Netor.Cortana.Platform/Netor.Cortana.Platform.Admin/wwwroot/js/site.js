(() => {
	const sidebar = document.getElementById("adminSidebar");
	const toggle = document.querySelector("[data-admin-toggle='sidebar']");

	if (!sidebar || !toggle) {
		return;
	}

	const syncSidebarState = () => {
		document.body.classList.toggle("admin-sidebar-open", sidebar.classList.contains("open"));
	};

	toggle.addEventListener("click", () => {
		if (window.innerWidth > 960) {
			document.body.classList.toggle("admin-sidebar-collapsed");
			return;
		}

		sidebar.classList.toggle("open");
		syncSidebarState();
	});

	document.addEventListener("click", (event) => {
		if (window.innerWidth > 960 || !sidebar.classList.contains("open")) {
			return;
		}

		const target = event.target;
		if (!(target instanceof Element)) {
			return;
		}

		if (sidebar.contains(target) || toggle.contains(target)) {
			return;
		}

		sidebar.classList.remove("open");
		syncSidebarState();
	});

	window.addEventListener("resize", () => {
		if (window.innerWidth > 960 && sidebar.classList.contains("open")) {
			sidebar.classList.remove("open");
			syncSidebarState();
		}
	});
})();

(() => {
	const masters = document.querySelectorAll("[data-check-all]");

	masters.forEach((master) => {
		const target = master.getAttribute("data-check-all");
		if (!target) {
			return;
		}

		master.addEventListener("change", () => {
			document.querySelectorAll(`[data-check-item='${target}']`).forEach((item) => {
				item.checked = master.checked;
			});
		});
	});
})();

(() => {
	if (!window.layui) {
		return;
	}

	layui.use(["element", "form"], function () {
		const element = layui.element;
		const form = layui.form;
		element.render();
		form.render();

		const checkAll = document.querySelectorAll("[data-check-all]");
		checkAll.forEach((item) => {
			item.addEventListener("change", () => {
				const target = item.getAttribute("data-check-all");
				if (!target) {
					return;
				}

				document.querySelectorAll(`[data-check-item='${target}']`).forEach((checkbox) => {
					checkbox.checked = item.checked;
				});
			});
		});
	});
})();
