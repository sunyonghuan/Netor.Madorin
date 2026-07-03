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
	if (!window.layui) {
		return;
	}

	const serializeForm = (form) => {
		const data = {};
		const formData = new FormData(form);

		formData.forEach((value, key) => {
			if (data[key] === undefined) {
				data[key] = value;
				return;
			}

			if (!Array.isArray(data[key])) {
				data[key] = [data[key]];
			}

			data[key].push(value);
		});

		return data;
	};

	const applyQueryToForm = (form) => {
		const params = new URLSearchParams(window.location.search);
		if (!Array.from(params.keys()).some((key) => key !== "mode")) {
			form.dataset.adminQueryHydrated = "true";
			return false;
		}

		let applied = false;
		params.forEach((value, key) => {
			if (key === "mode") {
				return;
			}

			Array.from(form.elements).forEach((field) => {
				if (!(field instanceof HTMLInputElement || field instanceof HTMLSelectElement || field instanceof HTMLTextAreaElement) || field.name !== key) {
					return;
				}

				if (field instanceof HTMLInputElement && (field.type === "checkbox" || field.type === "radio")) {
					field.checked = field.value === value || value === "true";
					applied = true;
					return;
				}

				field.value = value;
				applied = true;
			});
		});

		form.dataset.adminQueryHydrated = "true";
		return applied;
	};

	const hydrateFormsFromQuery = () => {
		document.querySelectorAll("form.layui-form").forEach((form) => {
			applyQueryToForm(form);
		});
	};

	const clearFilterQuery = (form) => {
		if (!window.history || !window.location.search) {
			return false;
		}

		const url = new URL(window.location.href);
		const previousUrl = `${url.pathname}${url.search}${url.hash}`;
		Array.from(form.elements).forEach((field) => {
			if (!(field instanceof HTMLInputElement || field instanceof HTMLSelectElement || field instanceof HTMLTextAreaElement) || !field.name) {
				return;
			}

			if (field instanceof HTMLInputElement && field.type === "hidden") {
				return;
			}

			url.searchParams.delete(field.name);
		});

		const nextUrl = `${url.pathname}${url.search}${url.hash}`;
		if (nextUrl === previousUrl) {
			return false;
		}

		window.history.replaceState({}, document.title, nextUrl);
		return true;
	};

	const isTopLevelWindow = () => {
		try {
			return window.self === window.top;
		} catch {
			return true;
		}
	};

	const getLayerIndex = () => {
		if (!parent || !parent.layer || !window.name) {
			return null;
		}

		return parent.layer.getFrameIndex(window.name);
	};

	const getDrawerApi = () => {
		try {
			if (window.parent && window.parent !== window && window.parent.adminDrawer) {
				return window.parent.adminDrawer;
			}
		} catch {
			return null;
		}

		return window.adminDrawer;
	};

	const appendDrawerPosition = (url, position) => {
		if (!url) {
			return url;
		}

		try {
			const resolved = new URL(url, window.location.origin);
			resolved.searchParams.set("drawerPosition", position);
			return resolved.origin === window.location.origin
				? `${resolved.pathname}${resolved.search}${resolved.hash}`
				: resolved.toString();
		} catch {
			return url;
		}
	};

	layui.use(["element", "form", "layer", "table", "dropdown", "laydate", "util"], function () {
		const element = layui.element;
		const form = layui.form;
		const layer = layui.layer;
		const table = layui.table;
		const dropdown = layui.dropdown;
		const laydate = layui.laydate;
		const util = layui.util;

		element.render();
		hydrateFormsFromQuery();
		form.render();

		if (window.__adminToast) {
			layer.msg(window.__adminToast);
			delete window.__adminToast;
		}

		window.adminFilter = {
			collect(selector) {
				const target = typeof selector === "string" ? document.querySelector(selector) : selector;
				if (!(target instanceof HTMLFormElement)) {
					return {};
				}

				if (target.dataset.adminQueryHydrated !== "true" && applyQueryToForm(target)) {
					form.render();
				}

				return serializeForm(target);
			},
			reset(tableId, formSelector) {
				const target = document.querySelector(formSelector);
				if (target instanceof HTMLFormElement) {
					target.reset();
					target.dataset.adminQueryHydrated = "true";
					const queryChanged = clearFilterQuery(target);
					form.render();

					if (queryChanged && isTopLevelWindow()) {
						window.location.reload();
						return;
					}
				}

				table.reload(tableId, { where: target instanceof HTMLFormElement ? serializeForm(target) : {}, page: { curr: 1 } });
			}
		};

		window.adminTable = {
			render(options) {
				const tableLimit = options.limit || 15;
				const tableLimits = options.limits || [10, 15, 20, 30, 50, 100];
				const userDone = options.done;
				const pageOptions = options.page === false
					? false
					: Object.assign({
						layout: ["prev", "page", "next", "skip", "count", "limit"],
						limit: tableLimit,
						limits: tableLimits,
						groups: 5
					}, typeof options.page === "object" ? options.page : {});

				const renderOptions = {
					elem: options.elem,
					id: options.id,
					url: options.url,
					method: options.method || "get",
					toolbar: options.toolbar ?? true,
					defaultToolbar: options.defaultToolbar ?? ["filter", "exports", "print"],
					page: pageOptions,
					limit: tableLimit,
					limits: tableLimits,
					cellMinWidth: options.cellMinWidth || 110,
					text: { none: options.emptyText || "暂无数据" },
					where: options.where || {},
					cols: options.cols,
					done(res, curr, count) {
						form.render("select");
						if (typeof userDone === "function") {
							userDone(res, curr, count);
						}
					}
				};

				if (options.height !== undefined && options.height !== null && options.height !== "") {
					renderOptions.height = options.height;
				}

				return table.render(renderOptions);
			},
			reload(id, where) {
				table.reload(id, {
					where: where || {},
					page: { curr: 1 }
				});
			}
		};

		window.adminDrawer = {
			openRight(options) {
				return layer.open({
					type: 2,
					title: options.title || "操作",
					content: appendDrawerPosition(options.url, "right"),
					area: [options.width || "760px", "100%"],
					offset: "r",
					anim: 5,
					shade: 0.25,
					shadeClose: true,
					resize: false,
					skin: "admin-layer-drawer admin-layer-drawer-right"
				});
			},
			openBottom(options) {
				return layer.open({
					type: 2,
					title: options.title || "明细",
					content: appendDrawerPosition(options.url, "bottom"),
					area: ["100%", options.height || "72%"],
					offset: "b",
					anim: 2,
					shade: 0.2,
					shadeClose: true,
					resize: false,
					skin: "admin-layer-drawer admin-layer-drawer-bottom"
				});
			},
			close(message, tableId) {
				const index = getLayerIndex();
				if (parent && parent.layui) {
					if (tableId && parent.layui.table) {
						parent.layui.table.reload(tableId);
					}

					if (message && parent.layui.layer) {
						parent.layui.layer.msg(message);
					}
				}

				if (index !== null) {
					parent.layer.close(index);
				}
			}
		};

		window.adminAction = {
			confirmSubmit(formElement, message) {
				layer.confirm(message || "确定执行该操作吗？", { icon: 3, title: "请确认" }, function (index) {
					formElement.dataset.confirmed = "true";
					if (typeof formElement.requestSubmit === "function") {
						formElement.requestSubmit();
					} else {
						formElement.submit();
					}
					layer.close(index);
				});
			}
		};

		const submitAdminAjaxForm = async (formElement, submitter) => {
			const submitButton = submitter instanceof HTMLButtonElement || submitter instanceof HTMLInputElement
				? submitter
				: formElement.querySelector("[type='submit']");
			const previousButtonText = submitButton ? submitButton.innerHTML : "";
			const loadingIndex = layer.load(2);

			if (submitButton) {
				submitButton.disabled = true;
				if (submitButton instanceof HTMLButtonElement) {
					submitButton.innerHTML = "提交中...";
				}
			}

			try {
				const response = await fetch(formElement.action || window.location.href, {
					method: formElement.method || "post",
					body: new FormData(formElement),
					headers: {
						"Accept": "application/json",
						"X-Requested-With": "XMLHttpRequest"
					}
				});
				const contentType = response.headers.get("content-type") || "";
				const result = contentType.includes("application/json")
					? await response.json()
					: { success: response.ok, message: await response.text() };

				if (!response.ok || result.success === false) {
					layer.msg(result.message || "提交失败，请检查表单内容。", { icon: 2 });
					return;
				}

				const message = result.message || formElement.dataset.adminSuccessMessage || "提交成功。";
				const tableId = result.tableId || formElement.dataset.adminTableId || "";
				if (!isTopLevelWindow() && window.adminDrawer) {
					window.adminDrawer.close(message, tableId);
					return;
				}

				layer.msg(message, { icon: 1 });
				if (tableId && table) {
					table.reload(tableId);
				}
			} catch {
				layer.msg("提交失败，请稍后重试。", { icon: 2 });
			} finally {
				layer.close(loadingIndex);
				delete formElement.dataset.confirmed;
				if (submitButton) {
					submitButton.disabled = false;
					if (submitButton instanceof HTMLButtonElement) {
						submitButton.innerHTML = previousButtonText;
					}
				}
			}
		};

		form.on("submit(adminAjaxSubmit)", function (data) {
			if (!(data.form instanceof HTMLFormElement) || data.form.dataset.adminAjaxForm !== "true") {
				return true;
			}

			const message = data.form.getAttribute("data-admin-confirm-submit");
			if (message && data.form.dataset.confirmed !== "true") {
				layer.confirm(message, { icon: 3, title: "请确认" }, function (index) {
					data.form.dataset.confirmed = "true";
					submitAdminAjaxForm(data.form, data.elem);
					layer.close(index);
				});
				return false;
			}

			submitAdminAjaxForm(data.form, data.elem);
			return false;
		});

		document.addEventListener("click", (event) => {
			const target = event.target instanceof Element ? event.target.closest("[data-admin-drawer]") : null;
			if (!target) {
				return;
			}

			event.preventDefault();
			const mode = target.getAttribute("data-admin-drawer");
			const url = target.getAttribute("data-url") || target.getAttribute("href");
			if (!url) {
				return;
			}

			const title = target.getAttribute("data-title") || target.getAttribute("title") || target.textContent?.trim();
			const drawerApi = getDrawerApi();
			if (!drawerApi) {
				return;
			}

			if (mode === "bottom") {
				drawerApi.openBottom({
					title,
					url,
					height: target.getAttribute("data-height") || undefined
				});
				return;
			}

			drawerApi.openRight({
				title,
				url,
				width: target.getAttribute("data-width") || undefined
			});
		});

		document.addEventListener("click", (event) => {
			const closeButton = event.target instanceof Element ? event.target.closest("[data-drawer-close]") : null;
			if (closeButton) {
				event.preventDefault();
				window.adminDrawer.close();
			}
		});

		document.addEventListener("click", (event) => {
			const refresh = event.target instanceof Element ? event.target.closest("[data-admin-refresh]") : null;
			if (refresh) {
				event.preventDefault();
				location.reload();
			}
		});

		document.addEventListener("click", (event) => {
			const fullscreen = event.target instanceof Element ? event.target.closest("[data-admin-fullscreen]") : null;
			if (!fullscreen) {
				return;
			}

			event.preventDefault();
			if (document.fullscreenElement) {
				document.exitFullscreen();
				return;
			}

			document.documentElement.requestFullscreen();
		});

		document.addEventListener("submit", (event) => {
			const formElement = event.target;
			if (!(formElement instanceof HTMLFormElement)) {
				return;
			}

			const message = formElement.getAttribute("data-admin-confirm-submit");
			if (!message || formElement.dataset.confirmed === "true") {
				return;
			}

			event.preventDefault();
			layer.confirm(message, { icon: 3, title: "请确认" }, function (index) {
				formElement.dataset.confirmed = "true";
				if (typeof formElement.requestSubmit === "function") {
					formElement.requestSubmit();
				} else {
					formElement.submit();
				}
				layer.close(index);
			});
		});

		document.addEventListener("submit", async (event) => {
			const formElement = event.target;
			if (!(formElement instanceof HTMLFormElement) || formElement.dataset.adminAjaxForm !== "true" || event.defaultPrevented) {
				return;
			}

			event.preventDefault();
			await submitAdminAjaxForm(formElement, event.submitter);
		});

		document.querySelectorAll("[data-laydate]").forEach((item) => {
			laydate.render({
				elem: item,
				type: item.getAttribute("data-laydate-type") || "date",
				range: item.hasAttribute("data-laydate-range")
			});
		});

		util.fixbar({ bgcolor: "#156f68" });

		window.layuiAdmin = { element, form, layer, table, dropdown, laydate, util };
	});
})();
