"use strict";
var KTDatatablesDataSourceHtml = function() {

	var initTable1 = function() {
		var table = $('#kt_datatable');

		// begin first table
		table.DataTable({
			responsive: true,
			columnDefs: [
				
				{
					//width: '75px',
					//targets: -2,
					//render: function(data, type, full, meta) {
					//	var status = {
					//		"False": {'title': 'Pasif', 'state': 'danger'},
					//		2: {'title': 'Retail', 'state': 'primary'},
					//		"True": {'title': 'Aktif', 'state': 'success'},
					//	};
					//	if (typeof status[data] === 'undefined') {
					//		return data;
					//	}
					//	return '<span class="label label-' + status[data].state + ' label-dot mr-2"></span>' +
					//		'<span class="font-weight-bold text-' + status[data].state + '">' + status[data].title + '</span>';
					//},
				}
			],
		});

	};

	return {

		//main function to initiate the module
		init: function() {
			initTable1();
		},

	};

}();

jQuery(document).ready(function() {
	KTDatatablesDataSourceHtml.init();
});
